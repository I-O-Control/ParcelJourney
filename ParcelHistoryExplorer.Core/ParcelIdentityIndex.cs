using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParcelHistoryExplorer.Core;

public sealed partial class ParcelIdentityIndex
{
    private static readonly JsonSerializerOptions sJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly ConcurrentDictionary<string, Task<ParcelIdentityIndex>> sInflightBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdentityNode> mNodes;

    private ParcelIdentityIndex(Dictionary<string, IdentityNode> nodes)
    {
        mNodes = nodes;
    }

    public static async Task<ParcelIdentityIndex> LoadOrBuildAsync(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        CancellationToken cancellationToken,
        IProgress<IndexBuildProgress>? progress = null)
    {
        var operationKey = CreateOperationKey(rootPath, filePatterns, includeSubdirectories);
        var buildTask = sInflightBuilds.GetOrAdd(
            operationKey,
            static (_, state) => LoadOrBuildCoreAsync(
                state.RootPath,
                state.FilePatterns,
                state.IncludeSubdirectories,
                state.CancellationToken,
                state.Progress),
            (RootPath: rootPath, FilePatterns: filePatterns, IncludeSubdirectories: includeSubdirectories, CancellationToken: cancellationToken, Progress: progress));

        try
        {
            return await buildTask.ConfigureAwait(false);
        }
        finally
        {
            if (buildTask.IsCompleted)
            {
                sInflightBuilds.TryRemove(operationKey, out _);
            }
        }
    }

    public static async Task WarmAsync(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        CancellationToken cancellationToken,
        IProgress<IndexBuildProgress>? progress = null)
    {
        _ = await LoadOrBuildAsync(rootPath, filePatterns, includeSubdirectories, cancellationToken, progress).ConfigureAwait(false);
    }

    public static async Task<ParcelIdentityIndex?> TryLoadAvailableAsync(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        CancellationToken cancellationToken)
    {
        var operationKey = CreateOperationKey(rootPath, filePatterns, includeSubdirectories);
        if (sInflightBuilds.TryGetValue(operationKey, out var inflightBuild) && inflightBuild.IsCompletedSuccessfully)
        {
            return await inflightBuild.ConfigureAwait(false);
        }

        if (!AppSafetyPolicy.PersistentCacheEnabled)
        {
            return null;
        }

        var files = EnumerateRelevantFiles(rootPath, filePatterns, includeSubdirectories).ToArray();
        var reusableSnapshots = await TryLoadReusableFileSnapshotsAsync(rootPath, files, cancellationToken).ConfigureAwait(false);
        return reusableSnapshots is not null && reusableSnapshots.Count == files.Length
            ? BuildFromFileSnapshots(reusableSnapshots.Values)
            : null;
    }

    private static async Task<ParcelIdentityIndex> LoadOrBuildCoreAsync(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        CancellationToken cancellationToken,
        IProgress<IndexBuildProgress>? progress)
    {
        progress?.Report(IndexBuildProgress.Indeterminate("Checking relevant log files..."));
        var files = EnumerateRelevantFiles(rootPath, filePatterns, includeSubdirectories).ToArray();
        if (!AppSafetyPolicy.PersistentCacheEnabled)
        {
            var freshSnapshots = await BuildOrReuseFileSnapshotsAsync(files, reusableSnapshots: null, cancellationToken, progress).ConfigureAwait(false);
            return BuildFromFileSnapshots(freshSnapshots.Values);
        }

        progress?.Report(IndexBuildProgress.Indeterminate($"Checking cached index for {files.Length} files..."));
        var reusableSnapshots = await TryLoadReusableFileSnapshotsAsync(rootPath, files, cancellationToken).ConfigureAwait(false);
        if (reusableSnapshots is not null && reusableSnapshots.Count == files.Length)
        {
            progress?.Report(IndexBuildProgress.Completed($"Identity index is ready for {files.Length} files."));
            return BuildFromFileSnapshots(reusableSnapshots.Values);
        }

        var allSnapshots = await BuildOrReuseFileSnapshotsAsync(files, reusableSnapshots, cancellationToken, progress).ConfigureAwait(false);
        var built = BuildFromFileSnapshots(allSnapshots.Values);
        var cachePath = GetCachePath(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await using var writeStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var snapshot = new IndexSnapshot("v2", allSnapshots.Values.OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray());
        await JsonSerializer.SerializeAsync(writeStream, snapshot, sJsonOptions, cancellationToken).ConfigureAwait(false);
        progress?.Report(IndexBuildProgress.Completed($"Identity index refreshed for {files.Length} files."));
        return built;
    }

    private static async Task<Dictionary<string, FileIndexSnapshot>?> TryLoadReusableFileSnapshotsAsync(
        string rootPath,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(rootPath);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var cachedStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var snapshot = await JsonSerializer.DeserializeAsync<IndexSnapshot>(cachedStream, sJsonOptions, cancellationToken).ConfigureAwait(false);
            if (snapshot?.Version != "v2" || snapshot.Files is not { Length: > 0 })
            {
                return null;
            }

            var cachedByPath = snapshot.Files.ToDictionary(static item => item.Path, StringComparer.OrdinalIgnoreCase);
            var reusable = new Dictionary<string, FileIndexSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (cachedByPath.TryGetValue(file, out var fileSnapshot)
                    && fileSnapshot.Length == info.Length
                    && fileSnapshot.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    reusable[file] = fileSnapshot;
                }
            }

            return reusable;
        }
        catch
        {
            return null;
        }
    }

    public IdentityResolution? Resolve(IReadOnlySet<string> seedTerms)
    {
        var queue = new Queue<string>(seedTerms.Where(static term => !string.IsNullOrWhiteSpace(term)));
        var relatedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parcelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime? firstSeen = null;
        DateTime? lastSeen = null;

        while (queue.Count > 0)
        {
            var token = queue.Dequeue();
            if (!relatedTokens.Add(token))
            {
                continue;
            }

            if (!mNodes.TryGetValue(token, out var node))
            {
                continue;
            }

            firstSeen = firstSeen is null || node.FirstSeenUtc < firstSeen ? node.FirstSeenUtc : firstSeen;
            lastSeen = lastSeen is null || node.LastSeenUtc > lastSeen ? node.LastSeenUtc : lastSeen;

            foreach (var file in node.Files)
            {
                files.Add(file);
            }

            if (node.SeenAsParcelId)
            {
                parcelIds.Add(node.Token);
            }

            if (node.SeenAsOrderId)
            {
                orderIds.Add(node.Token);
            }

            if (node.SeenAsTrackingId)
            {
                trackingIds.Add(node.Token);
            }

            foreach (var linkedToken in node.LinkedTokens)
            {
                if (!relatedTokens.Contains(linkedToken))
                {
                    queue.Enqueue(linkedToken);
                }
            }
        }

        return relatedTokens.Count == 0 || firstSeen is null || lastSeen is null
            ? null
            : new IdentityResolution(
                relatedTokens,
                files,
                firstSeen.Value,
                lastSeen.Value,
                parcelIds.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).FirstOrDefault(),
                orderIds.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).FirstOrDefault(),
                trackingIds.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).FirstOrDefault());
    }

    public IReadOnlyList<SearchInputCandidate> FindCandidatesContaining(string normalizedFragment, int maxCandidates, SearchCandidateKind kind = SearchCandidateKind.Any)
    {
        if (string.IsNullOrWhiteSpace(normalizedFragment))
        {
            return Array.Empty<SearchInputCandidate>();
        }

        return mNodes.Values
            .Where(static node => node.SeenAsParcelId || node.SeenAsOrderId || node.SeenAsTrackingId)
            .Where(node => MatchesKind(node, kind))
            .Where(node => node.Token.Contains(normalizedFragment, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => string.Equals(node.Token, normalizedFragment, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(node => node.Token.StartsWith(normalizedFragment, StringComparison.OrdinalIgnoreCase))
            .ThenBy(static node => node.Token.Length)
            .ThenBy(static node => node.Token, StringComparer.OrdinalIgnoreCase)
            .Take(maxCandidates)
            .Select(BuildCandidate)
            .ToArray();
    }

    private static async Task<Dictionary<string, FileIndexSnapshot>> BuildOrReuseFileSnapshotsAsync(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, FileIndexSnapshot>? reusableSnapshots,
        CancellationToken cancellationToken,
        IProgress<IndexBuildProgress>? progress)
    {
        var snapshots = new ConcurrentDictionary<string, FileIndexSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var reusable in reusableSnapshots ?? Enumerable.Empty<KeyValuePair<string, FileIndexSnapshot>>())
        {
            snapshots[reusable.Key] = reusable.Value;
        }

        var filesToRebuild = files.Where(file => reusableSnapshots is null || !reusableSnapshots.ContainsKey(file)).ToArray();
        var lastReportedPercentage = -1;
        var filesProcessed = 0;
        progress?.Report(IndexBuildProgress.Started(filesToRebuild.Length));

        if (filesToRebuild.Length == 0)
        {
            return snapshots.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        await Parallel.ForEachAsync(
            filesToRebuild,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
            },
            async (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                snapshots[file] = await BuildFileSnapshotAsync(file, token).ConfigureAwait(false);

                var processed = Interlocked.Increment(ref filesProcessed);
                var percentage = processed * 100 / filesToRebuild.Length;
                var previous = Interlocked.Exchange(ref lastReportedPercentage, percentage);
                if (previous != percentage)
                {
                    progress?.Report(IndexBuildProgress.Running(processed, filesToRebuild.Length));
                }
            }).ConfigureAwait(false);

        return snapshots.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<FileIndexSnapshot> BuildFileSnapshotAsync(string file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        var nodes = new Dictionary<string, MutableFileToken>(StringComparer.OrdinalIgnoreCase);

        using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || !line.Contains("SaveToDB - '", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = SaveToDbRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var timestamp = ParseTimestamp(line, info.LastWriteTime);
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderId = AddIfUseful(identifiers, match.Groups["orderId"].Value);
            var parcelId = AddIfUseful(identifiers, match.Groups["parcelId"].Value);
            var trackingId = AddIfUseful(identifiers, match.Groups["trackingId"].Value);
            if (identifiers.Count == 0)
            {
                continue;
            }

            foreach (var token in identifiers)
            {
                if (!nodes.TryGetValue(token, out var item))
                {
                    item = new MutableFileToken(token, timestamp);
                    nodes[token] = item;
                }

                if (timestamp < item.FirstSeenUtc)
                {
                    item.FirstSeenUtc = timestamp;
                }

                if (timestamp > item.LastSeenUtc)
                {
                    item.LastSeenUtc = timestamp;
                }

                item.SeenAsParcelId |= string.Equals(token, parcelId, StringComparison.OrdinalIgnoreCase);
                item.SeenAsOrderId |= string.Equals(token, orderId, StringComparison.OrdinalIgnoreCase);
                item.SeenAsTrackingId |= string.Equals(token, trackingId, StringComparison.OrdinalIgnoreCase);

                foreach (var linked in identifiers)
                {
                    if (!string.Equals(token, linked, StringComparison.OrdinalIgnoreCase))
                    {
                        item.LinkedTokens.Add(linked);
                    }
                }
            }
        }

        return new FileIndexSnapshot(
            file,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            nodes.Values
                .Select(static item => new FileTokenSnapshot(
                    item.Token,
                    item.LinkedTokens.OrderBy(static linked => linked, StringComparer.OrdinalIgnoreCase).ToArray(),
                    item.SeenAsParcelId,
                    item.SeenAsOrderId,
                    item.SeenAsTrackingId,
                    item.FirstSeenUtc.Ticks,
                    item.LastSeenUtc.Ticks))
                .ToArray());
    }

    private static string? AddIfUseful(HashSet<string> identifiers, string value)
    {
        var normalized = IdentifierExtractor.Normalize(value);
        if (IdentifierExtractor.IsUsefulParcelIdentity(normalized))
        {
            identifiers.Add(normalized);
            return normalized;
        }

        return null;
    }

    private static DateTime ParseTimestamp(string line, DateTime fallbackLocalTime)
    {
        if (line.Length >= 23
            && DateTime.TryParseExact(
                line[..23],
                "yyyy.MM.dd HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return fallbackLocalTime.ToUniversalTime();
    }

    private static string GetCachePath(string rootPath)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ParcelHistoryExplorer",
            "IndexCache");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootPath))).ToLowerInvariant();
        return Path.Combine(baseDir, $"{hash}.v2.json");
    }

    private static string CreateOperationKey(string rootPath, IReadOnlyList<string> filePatterns, bool includeSubdirectories)
    {
        var normalizedPatterns = filePatterns.Count == 0
            ? "*.txt;*.log"
            : string.Join(";", filePatterns.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase));
        return $"{rootPath}|{includeSubdirectories}|{normalizedPatterns}";
    }

    private static IEnumerable<string> EnumerateRelevantFiles(string rootPath, IReadOnlyList<string> filePatterns, bool includeSubdirectories)
    {
        var option = includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var patterns = filePatterns.Count == 0 ? ["*.txt", "*.log"] : filePatterns;

        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(rootPath, pattern, option))
            .Where(static path => IsRelevantProcessLog(Path.GetFileName(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsRelevantProcessLog(string fileName) =>
        fileName.StartsWith("ProcLogic", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesKind(IdentityNode node, SearchCandidateKind kind) =>
        kind switch
        {
            SearchCandidateKind.ParcelId => node.SeenAsParcelId,
            SearchCandidateKind.OrderId => node.SeenAsOrderId,
            SearchCandidateKind.TrackingId => node.SeenAsTrackingId,
            _ => true
        };

    private static SearchInputCandidate BuildCandidate(IdentityNode node)
    {
        var parts = new List<string>(3);
        if (node.SeenAsParcelId)
        {
            parts.Add("ParcelId");
        }

        if (node.SeenAsOrderId)
        {
            parts.Add("OrderId");
        }

        if (node.SeenAsTrackingId)
        {
            parts.Add("TrackingId");
        }

        var related = node.LinkedTokens
            .Where(linked => !string.Equals(linked, node.Token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        var description = related.Length == 0
            ? node.Token
            : $"{node.Token} | linked: {string.Join(", ", related)}";

        return new SearchInputCandidate(node.Token, string.Join(" / ", parts), description);
    }

    private static ParcelIdentityIndex BuildFromFileSnapshots(IEnumerable<FileIndexSnapshot> fileSnapshots)
    {
        var nodes = new Dictionary<string, IdentityNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileSnapshot in fileSnapshots)
        {
            foreach (var item in fileSnapshot.Tokens)
            {
                if (!nodes.TryGetValue(item.Token, out var node))
                {
                    node = new IdentityNode(item.Token, new DateTime(item.FirstSeenUtcTicks, DateTimeKind.Utc), new DateTime(item.LastSeenUtcTicks, DateTimeKind.Utc));
                    nodes[item.Token] = node;
                }

                var firstSeenUtc = new DateTime(item.FirstSeenUtcTicks, DateTimeKind.Utc);
                var lastSeenUtc = new DateTime(item.LastSeenUtcTicks, DateTimeKind.Utc);
                node.FirstSeenUtc = firstSeenUtc < node.FirstSeenUtc ? firstSeenUtc : node.FirstSeenUtc;
                node.LastSeenUtc = lastSeenUtc > node.LastSeenUtc ? lastSeenUtc : node.LastSeenUtc;
                node.Files.Add(fileSnapshot.Path);
                node.SeenAsParcelId |= item.SeenAsParcelId;
                node.SeenAsOrderId |= item.SeenAsOrderId;
                node.SeenAsTrackingId |= item.SeenAsTrackingId;

                foreach (var linked in item.LinkedTokens)
                {
                    node.LinkedTokens.Add(linked);
                }
            }
        }

        return new ParcelIdentityIndex(nodes);
    }

    [GeneratedRegex(@"SaveToDB - '(?:UPDATE|INSERT) \(tudata\) PrincipalID=[^,]*, OrderID=(?<orderId>[^,]*), ParcelID=(?<parcelId>[^,]*), .*?TrackingId=(?<trackingId>[^,]*), Status=(?<status>[^,]*),", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SaveToDbRegex();

    public sealed record IdentityResolution(
        IReadOnlySet<string> RelatedTokens,
        IReadOnlySet<string> Files,
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc,
        string? PreferredParcelId,
        string? PreferredOrderId,
        string? PreferredTrackingId);

    private sealed class IdentityNode
    {
        public IdentityNode(string token, DateTime firstSeenUtc, DateTime lastSeenUtc)
        {
            Token = token;
            FirstSeenUtc = firstSeenUtc;
            LastSeenUtc = lastSeenUtc;
        }

        public string Token { get; }
        public HashSet<string> LinkedTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public bool SeenAsParcelId { get; set; }
        public bool SeenAsOrderId { get; set; }
        public bool SeenAsTrackingId { get; set; }
    }

    private sealed class MutableFileToken
    {
        public MutableFileToken(string token, DateTime timestamp)
        {
            Token = token;
            FirstSeenUtc = timestamp;
            LastSeenUtc = timestamp;
        }

        public string Token { get; }
        public HashSet<string> LinkedTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public bool SeenAsParcelId { get; set; }
        public bool SeenAsOrderId { get; set; }
        public bool SeenAsTrackingId { get; set; }
    }

    private sealed record IndexSnapshot(string Version, FileIndexSnapshot[] Files);
    private sealed record FileIndexSnapshot(string Path, long Length, long LastWriteTimeUtcTicks, FileTokenSnapshot[] Tokens);
    private sealed record FileTokenSnapshot(string Token, string[] LinkedTokens, bool SeenAsParcelId, bool SeenAsOrderId, bool SeenAsTrackingId, long FirstSeenUtcTicks, long LastSeenUtcTicks);
}

public sealed record IndexBuildProgress(string Message, int FilesProcessed, int TotalFiles, bool IsCompleted, bool IsIndeterminate)
{
    public double Percentage => TotalFiles <= 0 ? 0 : (double)FilesProcessed / TotalFiles * 100d;

    public static IndexBuildProgress Indeterminate(string message) => new(message, 0, 0, false, true);
    public static IndexBuildProgress Started(int totalFiles) => new($"Refreshing identity index for {totalFiles} files...", 0, totalFiles, false, totalFiles <= 0);
    public static IndexBuildProgress Running(int filesProcessed, int totalFiles) => new($"Refreshing identity index... {filesProcessed}/{totalFiles} files", filesProcessed, totalFiles, false, false);
    public static IndexBuildProgress Completed(string message) => new(message, 0, 0, true, false);
}

public enum SearchCandidateKind
{
    Any,
    ParcelId,
    OrderId,
    TrackingId
}
