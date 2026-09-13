using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args.Where(a => a != "--no-browser").ToArray(), ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
// Loopback only, with an OS-assigned free port. No source shares or DB access.
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
builder.Services.AddRazorPages();
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
// Bundled resources keep the 3D prototype fully offline in the single-file release.
app.MapGet("/3d", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.Html"), "text/html"));
app.MapGet("/3d/viewer.css", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.Css"), "text/css"));
app.MapGet("/3d/viewer.js", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.Js"), "text/javascript"));
app.MapGet("/3d/model.json", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.Model"), "application/json"));
app.MapGet("/3d/license", () => Results.Content(EmbeddedAssets.Read("ParcelJourney.3d.License"), "text/plain"));
app.MapGet("/api/health", () => new { application = "ParcelJourney", ready = true, mode = "synthetic", runtime = Environment.Version.ToString() });
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
