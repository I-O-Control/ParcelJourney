using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using ParcelHistoryExplorer.Core;
using ParcelJourney.Core;
using ParcelJourney.Domain;
using ParcelJourney.App;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var captureArgument = Array.IndexOf(args, "--capture");
string? capturePath = captureArgument >= 0 && captureArgument + 1 < args.Length ? args[captureArgument + 1] : null;
var hostArgs = args.Where((a, i) => a != "--no-browser" && (captureArgument < 0 || i != captureArgument && i != captureArgument + 1)).ToArray();
if (capturePath is not null) CaptureReplay.Load(capturePath, false);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = hostArgs, ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
// Loopback only, with an OS-assigned free port. No source shares or DB access.
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.Limits.MaxRequestBodySize=64*1024*1024; });
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>o.MultipartBodyLengthLimit=64*1024*1024);
builder.Services.AddRazorPages();
builder.Services.AddSingleton<LogHistoryQueryEngine>();
builder.Services.AddSingleton<IParcelJourneyBuilder, ParcelJourneyBuilder>();
builder.Services.AddHostedService<LogIndexWarmupService>();
var app = builder.Build();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
app.Use(async (context, next) => {
    if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";
    await next();
});
app.MapRazorPages();
app.MapGet("/theme.css", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.Theme"), "text/css"));
// The 3D viewer returns here. Keep this route available in the packaged app as
// well as in the static demo, so both views always use the same curated 2D UI.
app.MapGet("/equipment-replay.html", () => Results.Content(CaptureReplay.AddLoader(CaptureReplay.Viewer()), "text/html"));
// Bundled resources keep the 3D prototype fully offline in the single-file release.
app.MapGet("/3d", () => Results.Content(CaptureReplay.AddLoader(EmbeddedAssets.Read("ParcelJourney.3d.Html")), "text/html"));
app.MapGet("/3d/viewer.css", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.Css"), "text/css"));
app.MapGet("/3d/viewer.js", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.Js"), "text/javascript"));
app.MapGet("/3d/model.json", () => Results.Content(CaptureReplay.Model, "application/json"));
app.MapGet("/3d/license", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.License"), "text/plain"));
app.MapGet("/api/capture/load", (string path, bool? closedOnly) => { try { return Results.Ok(CaptureReplay.Load(path, closedOnly ?? true)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/capture/upload", async (HttpRequest request) => {
    string temporary=Path.Combine(Path.GetTempPath(),"ParcelJourney-upload-"+Guid.NewGuid().ToString("N"));
    try {
        if(!request.HasFormContentType)return Results.BadRequest(new {error="Select a capture folder."});
        var form=await request.ReadFormAsync();if(form.Files.Count==0||form.Files.Count>10000)return Results.BadRequest(new {error="Invalid capture file count."});
        Directory.CreateDirectory(temporary);long total=0;
        foreach(var file in form.Files){total+=file.Length;if(total>64*1024*1024)throw new InvalidDataException("Capture upload exceeds 64 MiB.");
            var parts=file.FileName.Replace('\\','/').Split('/');if(parts.Any(p=>string.IsNullOrWhiteSpace(p)||p=="."||p==".."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("Invalid capture path.");
            string destination=Path.GetFullPath(Path.Combine(temporary,Path.Combine(parts)));if(!destination.StartsWith(temporary+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Capture path outside upload.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write);await file.CopyToAsync(output);
        }
        var indexes=Directory.GetFiles(temporary,"index.json",SearchOption.AllDirectories);if(indexes.Length!=1)throw new InvalidDataException("Select one Data folder containing index.json, dictionaries and parcels.");
        return Results.Ok(CaptureReplay.Load(Path.GetDirectoryName(indexes[0])!,false));
    }catch(Exception ex){return Results.BadRequest(new {error=ex.Message});}
    finally{if(Directory.Exists(temporary)&&Path.GetFileName(temporary).StartsWith("ParcelJourney-upload-",StringComparison.Ordinal))Directory.Delete(temporary,true);}
});
app.MapGet("/api/health", () => new { application = "ParcelJourney", ready = true, mode = "real-logs", runtime = Environment.Version.ToString() });
app.MapGet("/api/index/status", () => new { status = LogIndexWarmupService.Status });
app.MapGet("/api/pick-folder", async () =>
{
    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() => { try { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select the folder containing the log files", UseDescriptionForTitle = true, ShowNewFolderButton = false }; tcs.TrySetResult(dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null); } catch (Exception ex) { tcs.TrySetException(ex); } }) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA); thread.Start();
    try { var path = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5)); return path is null ? Results.NoContent() : Results.Ok(new { path }); }
    catch (Exception ex) { return Results.Problem($"Folder picker failed: {ex.GetBaseException().Message}"); }
});
app.MapGet("/api/journey", async (HttpRequest request, IParcelJourneyBuilder builder, CancellationToken cancellationToken) => {
    var logs = request.Query["logs"].ToString();
    var id = request.Query["id"].ToString();
    var patterns = request.Query["pattern"].Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).DefaultIfEmpty("*.log").ToArray();
    if (string.IsNullOrWhiteSpace(logs) || string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "Provide logs and id." });
    if (!Directory.Exists(logs)) return Results.NotFound(new { error = $"Log folder not found: {logs}" });
    var journey = await builder.BuildAsync(new JourneyQuery(id, Path.GetFullPath(logs), patterns, ForceFullLogScan: false), null, cancellationToken);
    return Results.Ok(journey);
});
app.MapGet("/api/session", () => new { token });
app.MapPost("/api/exit", (HttpContext context, IHostApplicationLifetime lifetime) => {
    if (context.Request.Headers["X-ParcelJourney-Session"] != token) return Results.StatusCode(403);
    context.Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
    return Results.Ok(new { stopped = true });
});
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
// Machine-readable discovery for local smoke tests; no sensitive parcel data.
var sessionPath = Path.Combine(Path.GetTempPath(), $"parceljourney-{Environment.ProcessId}.url");
await File.WriteAllTextAsync(sessionPath, address);
try {
    if (!args.Contains("--no-browser")) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    await app.WaitForShutdownAsync();
} finally {
    File.Delete(sessionPath);
    await app.DisposeAsync();
}

public static class EmbeddedAssets {
    public static string Read(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing bundled asset {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
