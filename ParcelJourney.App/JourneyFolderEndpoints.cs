namespace ParcelJourney.App;

public static class JourneyFolderEndpoints
{
    public static void Map(WebApplication app, string token)
    {
        app.MapGet("/api/journeys/preferences", () => {
            var p = JourneyFolders.ReadPreferences();
            return Results.Ok(new { folder=p.Folder, includeSubfolders=p.IncludeSubfolders, available=p.Folder is not null && Directory.Exists(p.Folder), loadedJourneys=CaptureReplay.LoadedCount });
        });
        app.MapPost("/api/journeys/preferences", (JourneyFolderPreferences p, HttpRequest request) => {
            if (request.Headers["X-ParcelJourney-Session"] != token) return Results.StatusCode(403);
            try { var old=JourneyFolders.ReadPreferences();var saved=JourneyFolders.SavePreferences(p);if(saved!=old)CaptureReplay.Clear();return Results.Ok(new { folder=saved.Folder, includeSubfolders=saved.IncludeSubfolders }); }
            catch (Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException) { return Results.BadRequest(new { error=e.Message }); }
        });
        app.MapGet("/api/journeys/catalogue", async (CancellationToken ct) => {
            try { var files=await JourneyFolders.CatalogueAsync(false,ct);return Results.Ok(new { files=files.Length, parcels=files.Select(f=>f.ParcelId).Distinct(StringComparer.OrdinalIgnoreCase).Count(), loadable=files.Count(f=>f.DatasetRoot is not null) }); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException) { return Results.BadRequest(new {error=e.Message}); }
        });
        app.MapGet("/api/journeys/suggest", async (string? prefix,CancellationToken ct) => {
            if(string.IsNullOrWhiteSpace(prefix)||prefix.Length<4||prefix.Length>128)return Results.Ok(Array.Empty<object>());
            try { var files=await JourneyFolders.CatalogueAsync(false,ct);return Results.Ok(files.Where(f=>f.ParcelId.StartsWith(prefix.Trim(),StringComparison.OrdinalIgnoreCase)).GroupBy(f=>f.ParcelId,StringComparer.OrdinalIgnoreCase).Take(25).Select(g=>new {parcelId=g.Key,files=g.Count(),loadable=g.Count(f=>f.DatasetRoot is not null)})); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException) { return Results.BadRequest(new {error=e.Message}); }
        });
        app.MapGet("/api/journeys/find", async (string? parcelId,CancellationToken ct) => {
            if(string.IsNullOrWhiteSpace(parcelId)||parcelId.Length>128)return Results.BadRequest(new {error="Enter a ParcelID."});
            try { var files=(await JourneyFolders.CatalogueAsync(true,ct)).Where(f=>f.ParcelId.Equals(parcelId.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();return Results.Ok(new {found=files.Length>0,files=files.Length,loadable=files.Count(f=>f.DatasetRoot is not null),loadedJourneys=CaptureReplay.LoadedCount}); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException) { return Results.BadRequest(new {error=e.Message}); }
        });
        app.MapPost("/api/journeys/load", async (LoadJourneyRequest selection,HttpRequest request,CancellationToken ct) => {
            if(request.Headers["X-ParcelJourney-Session"]!=token)return Results.StatusCode(403);
            try {
                var catalogue=await JourneyFolders.CatalogueAsync(true,ct);
                var matches=catalogue.Where(f=>selection.ParcelId is null||f.ParcelId.Equals(selection.ParcelId.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
                var files=matches.Where(f=>f.DatasetRoot is not null).ToArray();
                if(files.Length==0)return Results.BadRequest(new {error="No loadable published journeys found. The matching PJ01 index and dictionary are required."});
                var report=await Task.Run(()=>CaptureReplay.LoadFiles(files),ct);report["unindexedFilesSkipped"]=matches.Length-files.Length;
                return Results.Ok(report);
            }
            catch(Exception e) when(e is IOException or JourneyCapacityException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException) {return Results.BadRequest(new {error=e.Message});}
        });
        app.MapPost("/api/journeys/clear", (HttpRequest request) => {if(request.Headers["X-ParcelJourney-Session"]!=token)return Results.StatusCode(403);CaptureReplay.Clear();return Results.Ok(new {loadedJourneys=0});});
        app.MapPost("/api/journeys/pick-folder", async (HttpRequest request) => {
            if(request.Headers["X-ParcelJourney-Session"]!=token)return Results.StatusCode(403);
            var completion=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{
                try {using var dialog=new System.Windows.Forms.FolderBrowserDialog{Description="Choose a journey folder (selection does not load journeys)",UseDescriptionForTitle=true,ShowNewFolderButton=false};var saved=JourneyFolders.ReadPreferences().Folder;if(saved is not null&&Directory.Exists(saved))dialog.SelectedPath=saved;completion.TrySetResult(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK?dialog.SelectedPath:null);}
                catch(Exception e){completion.TrySetException(e);}
            }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
            try {var path=await completion.Task.WaitAsync(TimeSpan.FromMinutes(5));return path is null?Results.NoContent():Results.Ok(new {path});}
            catch(Exception e){return Results.BadRequest(new {error=e.GetBaseException().Message});}
        });
    }
    public sealed record LoadJourneyRequest(string? ParcelId = null);
}
