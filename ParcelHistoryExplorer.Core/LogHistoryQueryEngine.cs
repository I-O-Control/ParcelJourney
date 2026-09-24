using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ParcelHistoryExplorer.Core;

public sealed partial class LogHistoryQueryEngine
{
    private readonly DomainTimelineBuilder mTimelineBuilder = new();
    private const int MaxCorrelatedTokens = 8;
    private static readonly int MaxConcurrentFileScans = ResolveMaxConcurrentFileScans();
    private static readonly TimeSpan FileCandidateCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FileWindowPadding = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TrailingFileWindow = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, CachedFileCandidateSet> sFileCandidateCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] sFallbackLogGlobs =
    [
        "ProcPLC*.log",
        "ConPLCtoMFR*.log",
        "ConMFRtoPLC*.log",
        "ConMFRtoLVS*.log",
        "ConEti*.log",
        "ProcCamera*.log"
    ];

    private static int ResolveMaxConcurrentFileScans()
    {
        var configured = Environment.GetEnvironmentVariable("PHE_SCAN_THREADS");
        if (int.TryParse(configured, out var parsed) && parsed >= 1)
        {
            return Math.Clamp(parsed, 1, 16);
        }

        return Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    public async Task<ParcelHistorySearchResult> SearchAsync(ParcelHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Progress?.Report("Preparing search...");
        SearchTraceLog.Info("Search", $"Start Term='{query.SearchTerm}', Root='{query.LogRootPath}', ForceFullFileScan={query.ForceFullFileScan}, IncludeSubdirectories={query.IncludeSubdirectories}.");

        if (string.IsNullOrWhiteSpace(query.LogRootPath) || !Directory.Exists(query.LogRootPath))
        {
            SearchTraceLog.Info("Search", "Returning empty because log root path is invalid.");
            return ParcelHistorySearchResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            SearchTraceLog.Info("Search", "Returning empty because search term is blank.");
            return ParcelHistorySearchResult.Empty;
        }

        var searchTerm = query.SearchTerm.Trim();
        var searchProfile = SearchProfile.Create(searchTerm);
        var stopwatch = Stopwatch.StartNew();
        var rawHitsByLocation = new ConcurrentDictionary<string, DomainTimelineBuilder.RawLogHit>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<LogFileCandidate>? allFiles = null;
        IReadOnlyList<LogFileCandidate>? fallbackFiles = null;
        IReadOnlyList<LogFileCandidate> files;
        ParcelIdentityIndex.IdentityResolution? indexedIdentity = null;
        FileScanWindow? locatedWindow = null;
        ParcelDatabaseWindow? databaseWindow = null;

        if (searchProfile.RequiresExactIdentifierMatch && !query.ForceFullFileScan)
        {
            allFiles = BuildFileCandidates(query.LogRootPath, query.FilePatterns, query.IncludeSubdirectories);
            files = FilterLeanOperationalCandidates(allFiles);
            SearchTraceLog.Info("Search", $"Exact identifier mode: initial all-log candidate files={allFiles.Count}, lean candidate files={files.Count}.");
            query.Progress?.Report("Looking up parcel in DB...");

            var databaseLookup = await ParcelDatabaseLookup.TryResolveWindowAsync(searchProfile.PrimaryNormalizedTerm, cancellationToken).ConfigureAwait(false);
            databaseWindow = databaseLookup.Window;
            if (databaseLookup.Found && databaseWindow is not null)
            {
                SearchTraceLog.Info(
                    "Search",
                    $"Database time window hit: {databaseWindow.StartUtc:O} -> {databaseWindow.EndUtc:O}, Parcel='{databaseWindow.ParcelId}', Order='{databaseWindow.OrderId}', Tracking='{databaseWindow.TrackingId}', Status='{databaseWindow.Status}'.");
                var databaseIdentityTerms = BuildDatabaseIdentityTerms(databaseWindow, searchProfile.RawSearchTerm);
                var canonicalTerm = databaseWindow.ParcelId
                    ?? databaseWindow.OrderId
                    ?? databaseWindow.TrackingId
                    ?? searchProfile.PrimaryNormalizedTerm;
                searchProfile = searchProfile with
                {
                    PrimaryNormalizedTerm = canonicalTerm,
                    NormalizedTerms = databaseIdentityTerms,
                    PrefilterTerms = BuildPrefilterTerms(databaseIdentityTerms, searchProfile.RawSearchTerm)
                };
                SearchTraceLog.Info(
                    "Search",
                    $"DB identity canonicalization: PrimaryNormalizedTerm='{searchProfile.PrimaryNormalizedTerm}', Terms=[{string.Join(", ", searchProfile.NormalizedTerms)}].");
                query.Progress?.Report($"DB hit: {databaseWindow.StartUtc:yyyy-MM-dd HH:mm:ss} to {databaseWindow.EndUtc:yyyy-MM-dd HH:mm:ss}.");
            }
            else
            {
                SearchTraceLog.Info("Search", databaseLookup.Message);
                query.Progress?.Report(databaseLookup.Success
                    ? "No tudata entry found for this identifier. Search stopped."
                    : databaseLookup.Message);
                stopwatch.Stop();
                return BuildResult(
                    [],
                    filesScanned: 0,
                    linesScanned: 0,
                    duration: stopwatch.Elapsed,
                    normalizedSearchTerms: searchProfile.NormalizedTerms,
                    primarySearchTerm: searchProfile.PrimaryNormalizedTerm,
                    maxResults: query.MaxResults);
            }
        }
        else
        {
            allFiles = BuildFileCandidates(query.LogRootPath, query.FilePatterns, query.IncludeSubdirectories);
            files = allFiles;
            SearchTraceLog.Info("Search", $"General mode: initial candidate files={files.Count}.");
        }

        if (searchProfile.RequiresExactIdentifierMatch && !query.ForceFullFileScan)
        {
            try
            {
                var index = await ParcelIdentityIndex.TryLoadAvailableAsync(
                    query.LogRootPath,
                    query.FilePatterns,
                    query.IncludeSubdirectories,
                    cancellationToken).ConfigureAwait(false);
                if (index is null)
                {
                    SearchTraceLog.Info("Search", "Identity index is still warming; continuing exact search without indexed identity.");
                    query.Progress?.Report("Index not ready. Continuing without identity index.");
                }
                else
                {
                    indexedIdentity = index.Resolve(searchProfile.NormalizedTerms);
                    SearchTraceLog.Info(
                        "Search",
                        indexedIdentity is null
                            ? $"Index resolution miss for [{string.Join(", ", searchProfile.NormalizedTerms)}]."
                            : $"Index resolution hit. RelatedTokens={indexedIdentity.RelatedTokens.Count}, Files={indexedIdentity.Files.Count}, Parcel='{indexedIdentity.PreferredParcelId}', Order='{indexedIdentity.PreferredOrderId}', Tracking='{indexedIdentity.PreferredTrackingId}', FirstSeenUtc={indexedIdentity.FirstSeenUtc:O}, LastSeenUtc={indexedIdentity.LastSeenUtc:O}.");
                    query.Progress?.Report(indexedIdentity is null
                        ? "Index lookup found no linked identities."
                        : $"Index linked {indexedIdentity.RelatedTokens.Count} related identifiers.");
                }
            }
            catch (Exception ex)
            {
                indexedIdentity = null;
                SearchTraceLog.Error("Search", "Index resolution failed; continuing without indexed identity.", ex);
                query.Progress?.Report($"Index lookup failed: {ex.Message}");
            }
        }

        if (indexedIdentity is not null)
        {
            var effectiveSearchTerm = indexedIdentity.PreferredParcelId
                ?? indexedIdentity.PreferredOrderId
                ?? indexedIdentity.PreferredTrackingId
                ?? searchProfile.PrimaryNormalizedTerm;

            searchProfile = searchProfile with
            {
                PrimaryNormalizedTerm = effectiveSearchTerm,
                PrefilterTerms = BuildPrefilterTerms(indexedIdentity.RelatedTokens, searchProfile.RawSearchTerm)
            };
            files = FilterFileCandidates(files, indexedIdentity.Files);
            SearchTraceLog.Info("Search", $"After index file filter: files={files.Count}, PrimaryNormalizedTerm='{searchProfile.PrimaryNormalizedTerm}'.");
        }

        if (searchProfile.RequiresExactIdentifierMatch && !query.ForceFullFileScan)
        {
            locatedWindow = await TryLocateFastWindowAsync(
                query.LogRootPath,
                indexedIdentity?.RelatedTokens ?? searchProfile.NormalizedTerms,
                query.IncludeSubdirectories,
                cancellationToken).ConfigureAwait(false);
            SearchTraceLog.Info(
                "Search",
                locatedWindow is null
                    ? "Fast ProcLogic window lookup returned no hits."
                    : $"Fast ProcLogic window lookup hit: {locatedWindow.StartUtc:O} -> {locatedWindow.EndUtc:O}.");
        }

        var initialWindow = BuildInitialWindow(databaseWindow, indexedIdentity, locatedWindow, files);
        LogFileSelectionSummary(files, initialWindow, databaseWindow);
        query.Progress?.Report(
            initialWindow is null
                ? $"Scanning {files.Count} files without a time window..."
                : $"Scanning {CountFilesInWindow(files, initialWindow)} files in the time window...");

        var pass1 = await ScanAsync(
            files,
            indexedIdentity?.RelatedTokens ?? searchProfile.NormalizedTerms,
            searchProfile,
            rawHitsByLocation,
            query.MaxRawMatches,
            cancellationToken,
            initialWindow,
            query.ScanProgress,
            initialWindow is null ? "Scanning logs" : "Scanning DB time window").ConfigureAwait(false);

        var totalFilesScanned = pass1.FilesScanned;
        var totalLinesScanned = pass1.LinesScanned;
        SearchTraceLog.Info("Search", $"Pass1 complete: files={pass1.FilesScanned}, lines={pass1.LinesScanned}, rawHits={rawHitsByLocation.Count}.");
        LogPassFileUsage("Pass1", files, initialWindow, pass1);
        LogRawHitSummary(rawHitsByLocation.Values);

        if (searchProfile.RequiresExactIdentifierMatch
            && !query.ForceFullFileScan
            && rawHitsByLocation.Count == 0)
        {
            SearchTraceLog.Info("Search", "Exact search windowed pass returned no hits. Stopping without broad fallback.");
            query.Progress?.Report("No matching log events were found in the DB-derived time window.");
            stopwatch.Stop();
            return BuildResult(
                [],
                filesScanned: totalFilesScanned,
                linesScanned: totalLinesScanned,
                duration: stopwatch.Elapsed,
                normalizedSearchTerms: indexedIdentity?.RelatedTokens ?? searchProfile.NormalizedTerms,
                primarySearchTerm: searchProfile.PrimaryNormalizedTerm,
                maxResults: query.MaxResults);
        }

        if (searchProfile.RequiresExactIdentifierMatch
            && !query.ForceFullFileScan
            && rawHitsByLocation.Count > 0)
        {
            var exactIdentitySet = DiscoverIdentitySet(rawHitsByLocation.Values, indexedIdentity?.RelatedTokens ?? searchProfile.NormalizedTerms);
            SearchTraceLog.Info("Search", "Exact search returning after ProcLogic pass without fallback expansion.");
            stopwatch.Stop();
            return BuildResult(
                rawHitsByLocation.Values.ToList(),
                filesScanned: totalFilesScanned,
                linesScanned: totalLinesScanned,
                duration: stopwatch.Elapsed,
                normalizedSearchTerms: exactIdentitySet,
                primarySearchTerm: searchProfile.PrimaryNormalizedTerm,
                maxResults: query.MaxResults);
        }

        var indexedExpansionPending = indexedIdentity is not null
            && rawHitsByLocation.Count < query.MaxRawMatches
            && !HasTerminalSignal(rawHitsByLocation.Values);

        if (indexedExpansionPending)
        {
            var expansionIdentity = indexedIdentity!;
            fallbackFiles ??= BuildFileCandidates(
                query.LogRootPath,
                query.FilePatterns,
                query.IncludeSubdirectories,
                static fileName => IsFallbackRelevantLog(fileName));
            var expandedFiles = FilterIndexedExpansionCandidates(fallbackFiles, files);
            var locatedExpandedFiles = await TryLocateMatchingFilesAsync(
                query.LogRootPath,
                expansionIdentity.RelatedTokens,
                query.IncludeSubdirectories,
                sFallbackLogGlobs,
                cancellationToken).ConfigureAwait(false);
            if (locatedExpandedFiles is { Count: > 0 })
            {
                expandedFiles = expandedFiles
                    .Where(file => locatedExpandedFiles.Contains(file.Path))
                    .ToList();
            }

            SearchTraceLog.Info("Search", $"Indexed expansion pending: fallbackFiles={expandedFiles.Count}.");

            var passExpanded = await ScanAsync(
                expandedFiles,
                expansionIdentity.RelatedTokens,
                searchProfile,
                rawHitsByLocation,
                query.MaxRawMatches,
                cancellationToken,
                BuildExpandedWindow(initialWindow, expansionIdentity, expandedFiles),
                query.ScanProgress,
                "Scanning indexed fallback").ConfigureAwait(false);

            totalFilesScanned += passExpanded.FilesScanned;
            totalLinesScanned += passExpanded.LinesScanned;
            SearchTraceLog.Info("Search", $"Expanded pass complete: files={passExpanded.FilesScanned}, lines={passExpanded.LinesScanned}, rawHits={rawHitsByLocation.Count}.");
            LogPassFileUsage("Expanded", expandedFiles, BuildExpandedWindow(initialWindow, expansionIdentity, expandedFiles), passExpanded);
        }

        var correlatedTokens = DiscoverCorrelatedTokens(rawHitsByLocation.Values, searchProfile.PrimaryNormalizedTerm);

        if (indexedIdentity is null
            && searchProfile.AllowIdentityExpansion
            && correlatedTokens.Count > 1
            && rawHitsByLocation.Count < query.MaxRawMatches)
        {
            var optimizedWindow = query.ForceFullFileScan
                ? null
                : BuildOptimizedWindow(rawHitsByLocation.Values, files);
            var pass2 = await ScanAsync(
                files,
                correlatedTokens,
                searchProfile with { NormalizedTerms = correlatedTokens, PrefilterTerms = BuildPrefilterTerms(correlatedTokens, searchProfile.RawSearchTerm) },
                rawHitsByLocation,
                query.MaxRawMatches,
                cancellationToken,
                optimizedWindow,
                query.ScanProgress,
                optimizedWindow is null ? "Scanning correlated files" : "Scanning correlated window").ConfigureAwait(false);

            totalFilesScanned += pass2.FilesScanned;
            totalLinesScanned += pass2.LinesScanned;
            SearchTraceLog.Info("Search", $"Correlated pass complete: files={pass2.FilesScanned}, lines={pass2.LinesScanned}, rawHits={rawHitsByLocation.Count}, correlatedTokens=[{string.Join(", ", correlatedTokens)}].");
            LogPassFileUsage("Correlated", files, optimizedWindow, pass2);
        }

        var finalIdentitySet = DiscoverIdentitySet(rawHitsByLocation.Values, indexedIdentity?.RelatedTokens ?? searchProfile.NormalizedTerms);
        stopwatch.Stop();
        SearchTraceLog.Info(
            "Search",
            $"Complete: events={Math.Min(query.MaxResults, rawHitsByLocation.Count)}, rawHits={rawHitsByLocation.Count}, files={totalFilesScanned}, lines={totalLinesScanned}, durationMs={stopwatch.Elapsed.TotalMilliseconds:F0}, finalIdentityCount={finalIdentitySet.Count}.");
        query.Progress?.Report(rawHitsByLocation.Count == 0
            ? "Search finished with no matching timeline events."
            : $"Search finished with {Math.Min(query.MaxResults, rawHitsByLocation.Count)} matching events.");
        return BuildResult(rawHitsByLocation.Values.ToList(), totalFilesScanned, totalLinesScanned, stopwatch.Elapsed, finalIdentitySet, searchProfile.PrimaryNormalizedTerm, query.MaxResults);
    }

    private ParcelHistorySearchResult BuildResult(
        List<DomainTimelineBuilder.RawLogHit> rawHits,
        int filesScanned,
        int linesScanned,
        TimeSpan duration,
        IReadOnlySet<string> normalizedSearchTerms,
        string primarySearchTerm,
        int maxResults)
    {
        var events = mTimelineBuilder.Build(rawHits, normalizedSearchTerms, primarySearchTerm, maxResults);
        LogTimelineEventSourceSummary(events);
        return new ParcelHistorySearchResult(events, filesScanned, linesScanned, rawHits.Count, duration);
    }

    private async Task<ScanCounters> ScanAsync(
        IReadOnlyList<LogFileCandidate> files,
        IReadOnlySet<string> normalizedSearchTerms,
        SearchProfile searchProfile,
        ConcurrentDictionary<string, DomainTimelineBuilder.RawLogHit> rawHitsByLocation,
        int maxRawMatches,
        CancellationToken cancellationToken,
        FileScanWindow? fileWindow = null,
        IProgress<SearchProgressInfo>? scanProgress = null,
        string progressStage = "Scanning logs")
    {
        var candidateFiles = fileWindow is null
            ? files
            : files.Where(file => Overlaps(file, fileWindow)).ToArray();
        var filesScanned = 0;
        var linesScanned = 0;
        var progressStopwatch = Stopwatch.StartNew();
        var scannedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var hitCountsByFile = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long lastProgressMs = 0;

        if (candidateFiles.Count == 0)
        {
            return new ScanCounters(0, 0, Array.Empty<string>(), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scanToken = linkedCts.Token;
        var options = new ParallelOptions
        {
            CancellationToken = scanToken,
            MaxDegreeOfParallelism = MaxConcurrentFileScans
        };

        scanProgress?.Report(new SearchProgressInfo(progressStage, 0, candidateFiles.Count, 0, rawHitsByLocation.Count, TimeSpan.Zero));

        try
        {
            await Parallel.ForEachAsync(candidateFiles, options, async (file, token) =>
            {
                if (Volatile.Read(ref linesScanned) >= 0 && rawHitsByLocation.Count >= maxRawMatches)
                {
                    linkedCts.Cancel();
                    return;
                }

                Interlocked.Increment(ref filesScanned);
                scannedFiles.TryAdd(file.Path, 0);
                var fileTimestampUtc = file.TimestampUtc;

                await foreach (var line in ReadLinesAsync(file.Path, token).ConfigureAwait(false))
                {
                    if (rawHitsByLocation.Count >= maxRawMatches)
                    {
                        linkedCts.Cancel();
                        return;
                    }

                    Interlocked.Increment(ref linesScanned);

                    if (!LineMightMatch(line.Text, searchProfile, normalizedSearchTerms))
                    {
                        continue;
                    }

                    var parsedLine = LogLineParser.Parse(line.Text, file.Path, line.LineNumber, fileTimestampUtc);
                    if (!IsMatch(parsedLine, searchProfile, normalizedSearchTerms))
                    {
                        continue;
                    }

                    var key = $"{parsedLine.SourceFile}:{parsedLine.LineNumber}";
                    rawHitsByLocation[key] = new DomainTimelineBuilder.RawLogHit(
                        parsedLine.Timestamp,
                        parsedLine.Level,
                        parsedLine.Message,
                        parsedLine.SourceFile,
                        parsedLine.LineNumber,
                        parsedLine.Identifiers);
                    hitCountsByFile.AddOrUpdate(parsedLine.SourceFile, 1, static (_, current) => current + 1);

                    if (rawHitsByLocation.Count >= maxRawMatches)
                    {
                        linkedCts.Cancel();
                        return;
                    }
                }

                var completed = Volatile.Read(ref filesScanned);
                var elapsedMs = progressStopwatch.ElapsedMilliseconds;
                var previousMs = Interlocked.Read(ref lastProgressMs);
                if (completed == candidateFiles.Count || elapsedMs - previousMs >= 250)
                {
                    Interlocked.Exchange(ref lastProgressMs, elapsedMs);
                    scanProgress?.Report(new SearchProgressInfo(
                        progressStage,
                        completed,
                        candidateFiles.Count,
                        Volatile.Read(ref linesScanned),
                        rawHitsByLocation.Count,
                        progressStopwatch.Elapsed));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && rawHitsByLocation.Count >= maxRawMatches)
        {
        }

        return new ScanCounters(
            filesScanned,
            linesScanned,
            scannedFiles.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            hitCountsByFile.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> DiscoverCorrelatedTokens(IEnumerable<DomainTimelineBuilder.RawLogHit> rawHits, string primarySearchTerm)
    {
        var correlated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            primarySearchTerm
        };

        foreach (var token in rawHits
                     .SelectMany(static hit => hit.Identifiers)
                     .Where(IdentifierExtractor.IsUsefulParcelIdentity)
                     .OrderByDescending(static token => GetCorrelationPriority(token))
                     .ThenBy(static token => token, StringComparer.OrdinalIgnoreCase))
        {
            correlated.Add(token);
            if (correlated.Count >= MaxCorrelatedTokens)
            {
                break;
            }
        }

        return correlated;
    }

    private static HashSet<string> DiscoverIdentitySet(
        IEnumerable<DomainTimelineBuilder.RawLogHit> rawHits,
        IReadOnlySet<string> seedTerms)
    {
        var related = new HashSet<string>(
            seedTerms.Where(static token => !string.IsNullOrWhiteSpace(token)),
            StringComparer.OrdinalIgnoreCase);

        var hits = rawHits.ToList();
        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var usefulIdentifiers in hits
                         .Select(static hit => hit.Identifiers
                             .Where(IdentifierExtractor.IsUsefulParcelIdentity)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToArray())
                         .Where(static ids => ids.Length > 0))
            {
                if (!usefulIdentifiers.Any(related.Contains))
                {
                    continue;
                }

                foreach (var identifier in usefulIdentifiers)
                {
                    if (related.Add(identifier))
                    {
                        changed = true;
                    }
                }
            }
        }

        return related;
    }

    private static int GetCorrelationPriority(string token)
    {
        if (token.Length == 10 && token.All(char.IsDigit))
        {
            return 300;
        }

        if (token.Length > 10 && token.All(char.IsDigit))
        {
            return 250;
        }

        if (token.StartsWith("1Z", StringComparison.OrdinalIgnoreCase))
        {
            return 220;
        }

        return 100;
    }

    private static IEnumerable<string> EnumerateFiles(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        Func<string, bool>? fileNameFilter = null)
    {
        var option = includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var patterns = filePatterns.Count == 0 ? ["*.txt", "*.log"] : filePatterns;

        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(rootPath, pattern, option))
            .Where(path => fileNameFilter is null || fileNameFilter(Path.GetFileName(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<LogFileCandidate> BuildFileCandidates(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        Func<string, bool>? fileNameFilter = null)
    {
        var cacheKey = CreateFileCandidateCacheKey(rootPath, filePatterns, includeSubdirectories, fileNameFilter);
        if (sFileCandidateCache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.CreatedUtc <= FileCandidateCacheTtl)
        {
            return cached.Files;
        }

        var files = EnumerateFiles(rootPath, filePatterns, includeSubdirectories, fileNameFilter)
            .Select(static path => CreateFileCandidate(path))
            .ToList();

        var grouped = files
            .Where(static file => file.FileNameTimestampUtc is not null && file.StreamKey is not null)
            .GroupBy(static file => file.StreamKey!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var ordered = group
                .OrderBy(static file => file.FileNameTimestampUtc)
                .ThenBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                var current = ordered[index];
                var endUtc = index + 1 < ordered.Count
                    ? ordered[index + 1].FileNameTimestampUtc
                    : current.FileNameTimestampUtc!.Value + TrailingFileWindow;

                current.WindowStartUtc = current.FileNameTimestampUtc;
                current.WindowEndUtc = endUtc;
            }
        }

        sFileCandidateCache[cacheKey] = new CachedFileCandidateSet(DateTime.UtcNow, files);
        return files;
    }

    private static string CreateFileCandidateCacheKey(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        Func<string, bool>? fileNameFilter)
    {
        var filterKey = fileNameFilter is null
            ? "all"
            : fileNameFilter.Method.Name;
        return $"{rootPath}|{includeSubdirectories}|{filterKey}|{string.Join(";", filePatterns)}";
    }

    private static IReadOnlyList<LogFileCandidate> FilterFileCandidates(
        IReadOnlyList<LogFileCandidate> files,
        IReadOnlySet<string> allowedFiles)
    {
        if (allowedFiles.Count == 0)
        {
            return files
                .Where(static file => ParcelIdentityIndex.IsRelevantProcessLog(Path.GetFileName(file.Path)))
                .ToList();
        }

        var filtered = files
            .Where(file => allowedFiles.Contains(file.Path))
            .ToList();

        return filtered.Count > 0 ? filtered : files;
    }

    private static IReadOnlyList<LogFileCandidate> FilterLeanOperationalCandidates(IReadOnlyList<LogFileCandidate> files)
    {
        var filtered = files
            .Where(static file => IsLeanOperationalLog(Path.GetFileName(file.Path)))
            .ToList();

        return filtered.Count > 0 ? filtered : files;
    }

    private static bool IsLeanOperationalLog(string fileName)
    {
        return fileName.StartsWith("ProcLogicSC", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ProcPLCSC", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ConMFRtoPLCSC", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ConPLCtoMFRSC", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("RemoteManagement_Process", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ProcCamera_Process", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ConMFRtoCamera_Connection", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ProcEtikettierer_Process", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("ConEti3_Connection", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<LogFileCandidate> FilterIndexedExpansionCandidates(
        IReadOnlyList<LogFileCandidate> allFiles,
        IReadOnlyList<LogFileCandidate> initialFiles)
    {
        var initialPaths = new HashSet<string>(initialFiles.Select(static file => file.Path), StringComparer.OrdinalIgnoreCase);
        var expanded = allFiles
            .Where(file => !initialPaths.Contains(file.Path) && IsFallbackRelevantLog(Path.GetFileName(file.Path)))
            .ToList();

        return expanded.Count > 0 ? expanded : allFiles;
    }

    private static LogFileCandidate CreateFileCandidate(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var fileNameMetadata = TryParseFileNameTimestamp(fileName);
        var timestampUtc = fileNameMetadata.TimestampUtc ?? File.GetLastWriteTimeUtc(path);
        return new LogFileCandidate(path, timestampUtc, fileNameMetadata.TimestampUtc, fileNameMetadata.StreamKey);
    }

    private static FileScanWindow? BuildOptimizedWindow(IEnumerable<DomainTimelineBuilder.RawLogHit> rawHits, IReadOnlyList<LogFileCandidate> files)
    {
        var hits = rawHits.ToList();
        if (hits.Count == 0)
        {
            return null;
        }

        var minTimestamp = hits.Min(static hit => hit.Timestamp) - FileWindowPadding;
        var maxTimestamp = hits.Max(static hit => hit.Timestamp) + FileWindowPadding;
        var minUtc = minTimestamp.Kind == DateTimeKind.Utc ? minTimestamp : minTimestamp.ToUniversalTime();
        var maxUtc = maxTimestamp.Kind == DateTimeKind.Utc ? maxTimestamp : maxTimestamp.ToUniversalTime();

        foreach (var candidate in files.Where(static file => file.FileNameTimestampUtc is not null))
        {
            if (candidate.WindowStartUtc is null || candidate.WindowEndUtc is null)
            {
                continue;
            }

            if (candidate.WindowStartUtc.Value <= minUtc && candidate.WindowEndUtc.Value >= minUtc)
            {
                minUtc = candidate.WindowStartUtc.Value;
            }

            if (candidate.WindowStartUtc.Value <= maxUtc && candidate.WindowEndUtc.Value >= maxUtc)
            {
                maxUtc = candidate.WindowEndUtc.Value;
            }
        }

        return new FileScanWindow(minUtc, maxUtc);
    }

    private static FileScanWindow BuildIndexWindow(ParcelIdentityIndex.IdentityResolution resolution, IReadOnlyList<LogFileCandidate> files)
    {
        var minUtc = resolution.FirstSeenUtc - FileWindowPadding;
        var maxUtc = resolution.LastSeenUtc + FileWindowPadding;

        foreach (var candidate in files.Where(static file => file.FileNameTimestampUtc is not null))
        {
            if (candidate.WindowStartUtc is null || candidate.WindowEndUtc is null)
            {
                continue;
            }

            if (candidate.WindowStartUtc.Value <= minUtc && candidate.WindowEndUtc.Value >= minUtc)
            {
                minUtc = candidate.WindowStartUtc.Value;
            }

            if (candidate.WindowStartUtc.Value <= maxUtc && candidate.WindowEndUtc.Value >= maxUtc)
            {
                maxUtc = candidate.WindowEndUtc.Value;
            }
        }

        return new FileScanWindow(minUtc, maxUtc);
    }

    private static FileScanWindow BuildExpandedWindow(
        FileScanWindow? locatedWindow,
        ParcelIdentityIndex.IdentityResolution resolution,
        IReadOnlyList<LogFileCandidate> files)
    {
        if (locatedWindow is not null)
        {
            return ExpandWindow(locatedWindow, files);
        }

        return BuildIndexWindow(resolution, files);
    }

    private static FileScanWindow? BuildInitialWindow(
        ParcelDatabaseWindow? databaseWindow,
        ParcelIdentityIndex.IdentityResolution? indexedIdentity,
        FileScanWindow? locatedWindow,
        IReadOnlyList<LogFileCandidate> files)
    {
        FileScanWindow? initialWindow = databaseWindow is null
            ? null
            : new FileScanWindow(databaseWindow.StartUtc, databaseWindow.EndUtc);

        if (indexedIdentity is not null)
        {
            initialWindow = MergeWindows(initialWindow, BuildIndexWindow(indexedIdentity, files));
        }

        return MergeWindows(initialWindow, locatedWindow);
    }

    private static FileScanWindow ExpandWindow(FileScanWindow window, IReadOnlyList<LogFileCandidate> files)
    {
        var minUtc = window.StartUtc - FileWindowPadding;
        var maxUtc = window.EndUtc + FileWindowPadding;

        foreach (var candidate in files.Where(static file => file.FileNameTimestampUtc is not null))
        {
            if (candidate.WindowStartUtc is null || candidate.WindowEndUtc is null)
            {
                continue;
            }

            if (candidate.WindowStartUtc.Value <= minUtc && candidate.WindowEndUtc.Value >= minUtc)
            {
                minUtc = candidate.WindowStartUtc.Value;
            }

            if (candidate.WindowStartUtc.Value <= maxUtc && candidate.WindowEndUtc.Value >= maxUtc)
            {
                maxUtc = candidate.WindowEndUtc.Value;
            }
        }

        return new FileScanWindow(minUtc, maxUtc);
    }

    private static async Task<FileScanWindow?> TryLocateFastWindowAsync(
        string rootPath,
        IReadOnlySet<string> normalizedSearchTerms,
        bool includeSubdirectories,
        CancellationToken cancellationToken)
    {
        var terms = normalizedSearchTerms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0)
        {
            return null;
        }

        var rgPath = ResolveRipgrepPath();
        if (rgPath is null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = rgPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = rootPath
            };

            startInfo.ArgumentList.Add("--no-heading");
            startInfo.ArgumentList.Add("--with-filename");
            startInfo.ArgumentList.Add("--line-number");
            startInfo.ArgumentList.Add("--fixed-strings");
            startInfo.ArgumentList.Add("--glob");
            startInfo.ArgumentList.Add("ProcLogic*.log");

            if (!includeSubdirectories)
            {
                startInfo.ArgumentList.Add("--max-depth");
                startInfo.ArgumentList.Add("1");
            }

            foreach (var term in terms)
            {
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(term);
            }

            startInfo.ArgumentList.Add(rootPath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0 && process.ExitCode != 1)
            {
                return null;
            }

            return BuildLocatedWindow(output);
        }
        catch
        {
            return null;
        }
    }

    private static FileScanWindow? BuildLocatedWindow(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        DateTime? minUtc = null;
        DateTime? maxUtc = null;
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = Regex.Match(line, @"^(?<file>[A-Z]:.+?):(?<line>\d+):(?<text>.*)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var filePath = match.Groups["file"].Value;
            var text = match.Groups["text"].Value;
            if (!int.TryParse(match.Groups["line"].Value, out var lineNumber))
            {
                continue;
            }

            var fileTimestampUtc = File.GetLastWriteTimeUtc(filePath);
            var parsed = LogLineParser.Parse(text, filePath, lineNumber, fileTimestampUtc);
            var parsedUtc = parsed.Timestamp.Kind == DateTimeKind.Utc
                ? parsed.Timestamp
                : parsed.Timestamp.ToUniversalTime();

            minUtc = minUtc is null || parsedUtc < minUtc ? parsedUtc : minUtc;
            maxUtc = maxUtc is null || parsedUtc > maxUtc ? parsedUtc : maxUtc;
        }

        return minUtc is null || maxUtc is null
            ? null
            : new FileScanWindow(minUtc.Value, maxUtc.Value);
    }

    private static async Task<HashSet<string>?> TryLocateMatchingFilesAsync(
        string rootPath,
        IReadOnlySet<string> normalizedSearchTerms,
        bool includeSubdirectories,
        IReadOnlyList<string> globs,
        CancellationToken cancellationToken)
    {
        var terms = normalizedSearchTerms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0 || globs.Count == 0)
        {
            return null;
        }

        var rgPath = ResolveRipgrepPath();
        if (rgPath is null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = rgPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = rootPath
            };

            startInfo.ArgumentList.Add("--files-with-matches");
            startInfo.ArgumentList.Add("--fixed-strings");

            if (!includeSubdirectories)
            {
                startInfo.ArgumentList.Add("--max-depth");
                startInfo.ArgumentList.Add("1");
            }

            foreach (var glob in globs)
            {
                startInfo.ArgumentList.Add("--glob");
                startInfo.ArgumentList.Add(glob);
            }

            foreach (var term in terms)
            {
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(term);
            }

            startInfo.ArgumentList.Add(rootPath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0 && process.ExitCode != 1)
            {
                return null;
            }

            var files = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static path => Path.GetFullPath(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return files.Count == 0 ? null : files;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveRipgrepPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "rg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ripgrep", "rg.exe")
        };

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(directory, "rg.exe"));
                candidates.Add(Path.Combine(directory, "rg"));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!Path.IsPathRooted(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool HasTerminalSignal(IEnumerable<DomainTimelineBuilder.RawLogHit> rawHits)
    {
        foreach (var hit in rawHits)
        {
            if (hit.Message.Contains("Status=CLOSED", StringComparison.OrdinalIgnoreCase)
                || hit.Message.Contains("=> CLOSE", StringComparison.OrdinalIgnoreCase)
                || hit.Message.Contains("gibt es keinen Datensatz", StringComparison.OrdinalIgnoreCase)
                || hit.Message.Contains("Schicke Paket auf NIO", StringComparison.OrdinalIgnoreCase)
                || hit.Message.Contains("Ziel NIO", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static FileScanWindow? MergeWindows(FileScanWindow? primary, FileScanWindow? secondary)
    {
        if (primary is null)
        {
            return secondary;
        }

        if (secondary is null)
        {
            return primary;
        }

        var startUtc = primary.StartUtc <= secondary.StartUtc ? primary.StartUtc : secondary.StartUtc;
        var endUtc = primary.EndUtc >= secondary.EndUtc ? primary.EndUtc : secondary.EndUtc;
        return new FileScanWindow(startUtc, endUtc);
    }

    private static bool IsFallbackRelevantLog(string fileName) =>
        fileName.StartsWith("ProcPLC", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("ConPLCtoMFR", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("ConMFRtoPLC", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("ConMFRtoLVS", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("ConEti", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("ProcCamera", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(LogFileCandidate file, FileScanWindow window)
    {
        if (file.WindowStartUtc is null || file.WindowEndUtc is null)
        {
            return true;
        }

        return file.WindowStartUtc.Value <= window.EndUtc && file.WindowEndUtc.Value >= window.StartUtc;
    }

    private static int CountFilesInWindow(IReadOnlyList<LogFileCandidate> files, FileScanWindow window) =>
        files.Count(file => Overlaps(file, window) || file.WindowStartUtc is null || file.WindowEndUtc is null);

    private static void LogFileSelectionSummary(
        IReadOnlyList<LogFileCandidate> files,
        FileScanWindow? initialWindow,
        ParcelDatabaseWindow? databaseWindow)
    {
        if (initialWindow is null)
        {
            SearchTraceLog.Info("Search", $"File selection: no window applied. Candidate files={files.Count}.");
            return;
        }

        var selected = files
            .Where(file => Overlaps(file, initialWindow) || file.WindowStartUtc is null || file.WindowEndUtc is null)
            .ToList();
        var archived = selected.Where(static file => file.FileNameTimestampUtc is not null).ToList();
        var live = selected.Where(static file => file.FileNameTimestampUtc is null).ToList();
        var grouped = selected
            .GroupBy(static file => file.StreamKey ?? "<live-or-unknown>", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .Take(12)
            .Select(static group => $"{group.Key}:{group.Count()}")
            .ToArray();
        var sampleStart = selected.Take(5).Select(static file => Path.GetFileName(file.Path)).ToArray();
        var sampleEnd = selected.TakeLast(5).Select(static file => Path.GetFileName(file.Path)).ToArray();

        SearchTraceLog.Info(
            "Search",
            $"File selection: dbWindow={(databaseWindow is null ? "<none>" : $"{databaseWindow.StartUtc:O}->{databaseWindow.EndUtc:O}")}, effectiveWindow={initialWindow.StartUtc:O}->{initialWindow.EndUtc:O}, selected={selected.Count}, archived={archived.Count}, liveOrUnparsed={live.Count}, topStreams=[{string.Join(", ", grouped)}], firstFiles=[{string.Join(", ", sampleStart)}], lastFiles=[{string.Join(", ", sampleEnd)}].");
    }

    private static void LogRawHitSummary(IEnumerable<DomainTimelineBuilder.RawLogHit> rawHits)
    {
        var hits = rawHits.OrderBy(static hit => hit.Timestamp).ToList();
        if (hits.Count == 0)
        {
            SearchTraceLog.Info("Search", "Raw hit summary: no matching log hits.");
            return;
        }

        var statuses = hits
            .Select(static hit => Regex.Match(hit.Message, @"Status=(?<status>[A-Z0-9_]+)", RegexOptions.IgnoreCase))
            .Where(static match => match.Success)
            .Select(static match => match.Groups["status"].Value.ToUpperInvariant())
            .GroupBy(static status => status, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .Select(static group => $"{group.Key}:{group.Count()}")
            .ToArray();

        var tail = hits
            .TakeLast(8)
            .Select(static hit => $"{hit.Timestamp:yyyy-MM-dd HH:mm:ss} {Path.GetFileName(hit.SourceFile)}:{hit.LineNumber} {TrimForTrace(hit.Message)}")
            .ToArray();

        SearchTraceLog.Info(
            "Search",
            $"Raw hit summary: first={hits.First().Timestamp:O}, last={hits.Last().Timestamp:O}, statuses=[{string.Join(", ", statuses)}], tail=[{string.Join(" || ", tail)}].");
    }

    private static void LogPassFileUsage(
        string passName,
        IReadOnlyList<LogFileCandidate> files,
        FileScanWindow? fileWindow,
        ScanCounters counters)
    {
        var associated = fileWindow is null
            ? files
            : files.Where(file => Overlaps(file, fileWindow)).ToList();
        var associatedPaths = associated.Select(static file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var usedPaths = counters.HitCountsByFile.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var usedDetails = counters.HitCountsByFile
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{Path.GetFileName(pair.Key)}:{pair.Value}")
            .ToArray();

        SearchTraceLog.Info(
            "Search",
            $"{passName} file usage: associatedCount={associatedPaths.Length}, scannedCount={counters.ScannedFiles.Count}, usedCount={usedPaths.Length}, associatedStreams=[{FormatStreamSummary(associatedPaths)}], scannedStreams=[{FormatStreamSummary(counters.ScannedFiles)}], usedStreams=[{FormatStreamSummary(usedPaths)}], usedHitCounts=[{string.Join(", ", usedDetails)}], associatedFiles=[{string.Join(", ", associatedPaths.Select(Path.GetFileName))}], scannedFiles=[{string.Join(", ", counters.ScannedFiles.Select(Path.GetFileName))}], usedFiles=[{string.Join(", ", usedPaths.Select(Path.GetFileName))}].");
    }

    private static void LogTimelineEventSourceSummary(IReadOnlyList<ParcelTimelineEvent> events)
    {
        if (events.Count == 0)
        {
            SearchTraceLog.Info("Search", "Timeline event sources: no events were produced.");
            return;
        }

        var eventSources = events
            .GroupBy(static item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => $"{Path.GetFileName(group.Key)}:{group.Count()}")
            .ToArray();
        var eventFiles = events
            .Select(static item => item.SourceFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .ToArray();

        SearchTraceLog.Info(
            "Search",
            $"Timeline event sources: fileCount={eventFiles.Length}, streams=[{FormatStreamSummary(events.Select(static item => item.SourceFile))}], perFile=[{string.Join(", ", eventSources)}], files=[{string.Join(", ", eventFiles)}].");
    }

    private static string FormatStreamSummary(IEnumerable<string> filePaths)
    {
        var groups = filePaths
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(static name => GetTraceStreamName(name), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => $"{group.Key}:{group.Count()}")
            .ToArray();
        return string.Join(", ", groups);
    }

    private static string GetTraceStreamName(string fileNameWithoutExtension)
    {
        var match = FileNameTimestampRegex().Match(fileNameWithoutExtension);
        return match.Success ? match.Groups["stream"].Value : fileNameWithoutExtension;
    }

    private static string TrimForTrace(string message)
    {
        const int maxLength = 180;
        if (message.Length <= maxLength)
        {
            return message;
        }

        return message[..maxLength] + "...";
    }

    [GeneratedRegex(@"^(?<stream>.+?)_(?<timestamp>\d{4}_\d{2}_\d{2}__\d{2}_\d{2}_\d{2})$", RegexOptions.Compiled)]
    private static partial Regex FileNameTimestampRegex();

    private static (DateTime? TimestampUtc, string? StreamKey) TryParseFileNameTimestamp(string fileName)
    {
        var match = FileNameTimestampRegex().Match(fileName);
        if (!match.Success)
        {
            return (null, null);
        }

        var timestampText = match.Groups["timestamp"].Value;
        if (!DateTime.TryParseExact(
                timestampText,
                "yyyy_MM_dd__HH_mm_ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return (null, null);
        }

        return (parsed.ToUniversalTime(), match.Groups["stream"].Value);
    }

    private static bool IsMatch(LogLineParser.ParsedLogLine timelineEvent, SearchProfile searchProfile, IReadOnlySet<string> normalizedSearchTerms)
    {
        if (timelineEvent.Identifiers.Any(normalizedSearchTerms.Contains))
        {
            return true;
        }

        if (searchProfile.RequiresExactIdentifierMatch)
        {
            return false;
        }

        return timelineEvent.Message.Contains(searchProfile.RawSearchTerm, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LineMightMatch(string line, SearchProfile searchProfile, IReadOnlySet<string> normalizedSearchTerms)
    {
        if (searchProfile.PrefilterTerms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (searchProfile.RequiresExactIdentifierMatch)
        {
            return false;
        }

        var normalizedLine = IdentifierExtractor.Normalize(line);
        var preservedLine = IdentifierExtractor.NormalizePreservingTrackingChars(line);
        return normalizedSearchTerms.Any(normalizedLine.Contains)
            || normalizedSearchTerms.Any(preservedLine.Contains);
    }

    private static string[] BuildPrefilterTerms(IEnumerable<string> normalizedTerms, string rawSearchTerm)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(rawSearchTerm))
        {
            terms.Add(rawSearchTerm.Trim());
        }

        foreach (var token in normalizedTerms)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                terms.Add(token);
            }
        }

        return terms
            .OrderByDescending(static term => term.Length)
            .ToArray();
    }

    private static IReadOnlySet<string> BuildDatabaseIdentityTerms(ParcelDatabaseWindow databaseWindow, string rawSearchTerm)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in new[] { rawSearchTerm, databaseWindow.ParcelId, databaseWindow.OrderId, databaseWindow.TrackingId })
        {
            foreach (var token in IdentifierExtractor.ExpandSearchTokens(value ?? string.Empty))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    terms.Add(token);
                }
            }
        }

        return terms;
    }

    private static async IAsyncEnumerable<LogFileLine> ReadLinesAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);

        var lineNumber = 0;
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                continue;
            }

            lineNumber++;
            yield return new LogFileLine(lineNumber, line);
        }
    }

    private sealed record LogFileLine(int LineNumber, string Text);

    private sealed record ScanCounters(
        int FilesScanned,
        int LinesScanned,
        IReadOnlyList<string> ScannedFiles,
        IReadOnlyDictionary<string, int> HitCountsByFile);

    private sealed class LogFileCandidate
    {
        public LogFileCandidate(string path, DateTime timestampUtc, DateTime? fileNameTimestampUtc, string? streamKey)
        {
            Path = path;
            TimestampUtc = timestampUtc;
            FileNameTimestampUtc = fileNameTimestampUtc;
            StreamKey = streamKey;
        }

        public string Path { get; }

        public DateTime TimestampUtc { get; }

        public DateTime? FileNameTimestampUtc { get; }

        public string? StreamKey { get; }

        public DateTime? WindowStartUtc { get; set; }

        public DateTime? WindowEndUtc { get; set; }
    }

    private sealed record CachedFileCandidateSet(DateTime CreatedUtc, IReadOnlyList<LogFileCandidate> Files);

    private sealed record FileScanWindow(DateTime StartUtc, DateTime EndUtc);

    private sealed record SearchProfile(
        string RawSearchTerm,
        string PrimaryNormalizedTerm,
        IReadOnlySet<string> NormalizedTerms,
        IReadOnlyList<string> PrefilterTerms,
        bool RequiresExactIdentifierMatch,
        bool AllowIdentityExpansion)
    {
        public static SearchProfile Create(string rawSearchTerm)
        {
            var normalizedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryNormalizedTerm = IdentifierExtractor.NormalizeSearchInput(rawSearchTerm);

            foreach (var token in IdentifierExtractor.ExpandSearchTokens(rawSearchTerm))
            {
                normalizedTerms.Add(token);
            }

            var requiresExactIdentifierMatch = normalizedTerms.Any(IdentifierExtractor.IsUsefulParcelIdentity);
            return new SearchProfile(
                rawSearchTerm,
                primaryNormalizedTerm,
                normalizedTerms,
                BuildPrefilterTerms(normalizedTerms, rawSearchTerm),
                requiresExactIdentifierMatch,
                AllowIdentityExpansion: normalizedTerms.Count > 0);
        }
    }
}
