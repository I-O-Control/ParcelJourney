using System.Text.Json;
using System.Text.Json.Nodes;
using ParcelJourney.Core;

namespace ParcelJourney.App;

public sealed record JourneyFolderPreferences(string? Folder = null, bool IncludeSubfolders = true);
public sealed record JourneyFile(string ParcelId, string JourneyKey, string File, string? DatasetRoot);

public static class JourneyFolders
{
    private static readonly object SettingsLock = new();
    private static readonly SemaphoreSlim CatalogueLock = new(1, 1);
    private static string? cacheKey;
    private static DateTime cachedAt;
    private static JourneyFile[] cached = [];
    public static string SettingsPath => Environment.GetEnvironmentVariable("PARCELJOURNEY_SETTINGS_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParcelJourney", "viewer-settings.json");

    public static JourneyFolderPreferences ReadPreferences()
    {
        lock (SettingsLock)
        {
            try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<JourneyFolderPreferences>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    public static JourneyFolderPreferences SavePreferences(JourneyFolderPreferences preferences)
    {
        var folder = string.IsNullOrWhiteSpace(preferences.Folder) ? null : Path.GetFullPath(preferences.Folder.Trim());
        if (folder is not null && !Directory.Exists(folder)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        preferences = preferences with { Folder = folder };
        lock (SettingsLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
        }
        return preferences;
    }

    public static string? DatasetRoot(string file)
    {
        var directory = Directory.Exists(file) ? Path.GetFullPath(file) : Path.GetDirectoryName(Path.GetFullPath(file));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "index.json")))
            {
                try { if (JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "index.json")))?["format"]?.ToString() == "PJ01-index") return directory; }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }

    public static async Task<JourneyFile[]> CatalogueAsync(bool refresh, CancellationToken cancellationToken)
    {
        var preferences = ReadPreferences();
        if (preferences.Folder is null || !Directory.Exists(preferences.Folder)) return [];
        var key = preferences.Folder + "|" + preferences.IncludeSubfolders;
        await CatalogueLock.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && cacheKey == key && DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(15)) return cached;
            var entries = await Task.Run(() => Scan(preferences, cancellationToken), cancellationToken);
            cached = entries; cacheKey = key; cachedAt = DateTime.UtcNow;
            return entries;
        }
        finally { CatalogueLock.Release(); }
    }

    private static JourneyFile[] Scan(JourneyFolderPreferences preferences, CancellationToken cancellationToken)
    {
        var root = preferences.Folder!;
        var options = new EnumerationOptions { RecurseSubdirectories = preferences.IncludeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var indexes = Directory.EnumerateFiles(root, "index.json", options).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ancestor = DatasetRoot(root);
        if (ancestor is not null) indexes.Add(Path.Combine(ancestor, "index.json"));
        var result = new Dictionary<string, JourneyFile>(StringComparer.OrdinalIgnoreCase);
        var indexedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool InSelection(string file) => preferences.IncludeSubfolders
            ? file.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetDirectoryName(file), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        foreach (var indexFile in indexes.OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(indexFile).Length > 32 * 1024 * 1024) continue;
                var index = JsonNode.Parse(File.ReadAllText(indexFile));
                if (index?["format"]?.ToString() != "PJ01-index" || index["entries"] is not JsonArray rows) continue;
                var dataset = Path.GetDirectoryName(indexFile)!; indexedRoots.Add(dataset);
                foreach (var entry in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = entry?["file"]?.ToString(); var id = entry?["parcel"]?.ToString();
                    if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(id) || Path.IsPathRooted(relative)) continue;
                    var file = Path.GetFullPath(Path.Combine(dataset, relative));
                    if (!file.StartsWith(dataset + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !InSelection(file) || !File.Exists(file)) continue;
                    result[file] = new(id, entry?["journey"]?.ToString() ?? id, file, dataset);
                    if (result.Count > 100000) throw new InvalidDataException("Too many journey files. Select a more specific folder.");
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        // Unindexed files can be found from their header without decoding events.
        // They remain unloadable until the exact index/dictionary is available.
        foreach (var file in Directory.EnumerateFiles(root, "*.pjourney", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ContainsKey(file) || indexedRoots.Any(d => file.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var header = PjourneyReader.ReadHeader(file); var id = header["parcel"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) result[file] = new(id, header["visit_id"]?.ToString() ?? id, file, null);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or OverflowException) { }
            if (result.Count > 100000) throw new InvalidDataException("Too many journey files. Select a more specific folder.");
        }
        return result.Values.OrderBy(e => e.ParcelId, StringComparer.Ordinal).ThenBy(e => e.File, StringComparer.Ordinal).ToArray();
    }
}
