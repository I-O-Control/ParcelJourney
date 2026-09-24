namespace ParcelHistoryExplorer.Core;

public sealed record ParcelHistoryQuery(
    string LogRootPath,
    string SearchTerm,
    IReadOnlyList<string> FilePatterns,
    bool IncludeSubdirectories = true,
    bool ForceFullFileScan = false,
    int MaxResults = 200,
    int MaxRawMatches = 5000,
    IProgress<string>? Progress = null,
    IProgress<SearchProgressInfo>? ScanProgress = null);

public sealed record SearchProgressInfo(
    string Stage,
    int FilesCompleted,
    int TotalFiles,
    int LinesScanned,
    int RawMatches,
    TimeSpan Elapsed,
    bool IsIndeterminate = false);

public sealed record ParcelTimelineEvent(
    DateTime Timestamp,
    string Phase,
    string Stage,
    string Summary,
    string Details,
    string Level,
    string Source,
    string SourceFile,
    int LineNumber,
    string? ParcelId,
    string? OrderId,
    string? TrackingId,
    string? ScannerName,
    string? ScannerId,
    string? Target,
    IReadOnlyList<string> Identifiers,
    int OccurrenceCount)
{
    public string SourceSummary => $"{Path.GetFileName(SourceFile)}:{LineNumber}";
}

public sealed record ParcelHistorySearchResult(
    IReadOnlyList<ParcelTimelineEvent> Events,
    int FilesScanned,
    int LinesScanned,
    int RawMatches,
    TimeSpan Duration)
{
    public static readonly ParcelHistorySearchResult Empty =
        new(Array.Empty<ParcelTimelineEvent>(), 0, 0, 0, TimeSpan.Zero);
}
