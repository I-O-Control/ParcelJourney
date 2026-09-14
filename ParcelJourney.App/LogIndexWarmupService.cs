using ParcelHistoryExplorer.Core;

namespace ParcelJourney.App;

public sealed class LogIndexWarmupService : BackgroundService
{
    private readonly ILogger<LogIndexWarmupService> _logger;
    public static string Status { get; private set; } = "No source folder configured.";

    public LogIndexWarmupService(ILogger<LogIndexWarmupService> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Environment.GetEnvironmentVariable("PARCELJOURNEY_SOURCE_FOLDER");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            Status = $"Indexing {root}…";
            await ParcelIdentityIndex.WarmAsync(Path.GetFullPath(root), ["*.log", "*.txt"], true, stoppingToken);
            Status = $"Index ready for {root}";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { Status = $"Index failed: {ex.Message}"; _logger.LogError(ex, "Log index warmup failed"); }
    }
}
