using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ParcelJourney.Core;

/// <summary>Lossless compact v2 decoder. Invalid data fails explicitly; unknown fields survive.</summary>
internal static class CompactJourneyReader
{
    internal static JsonObject Decode(JsonElement feed)
    {
        if (feed.GetProperty("version").GetInt32() != 2)
            throw new InvalidDataException("Unsupported compact schema. Regenerate legacy v1 files; they lost evidence.");
        var columns = feed.GetProperty("columns").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var dictionaries = feed.GetProperty("dict");
        if (dictionaries.GetArrayLength() != columns.Length || columns.Distinct().Count() != columns.Length)
            throw new InvalidDataException("Invalid compact columns.");
        var output = JsonNode.Parse(feed.GetProperty("metadata").GetRawText())!.AsObject();
        var events = new JsonArray();
        foreach (var row in feed.GetProperty("rows").EnumerateArray())
        {
            if (row.GetArrayLength() != columns.Length) throw new InvalidDataException("Invalid compact row width.");
            var item = new JsonObject();
            for (var i = 0; i < columns.Length; i++)
            {
                var reference = row[i].GetInt32();
                if (reference < -1 || reference >= dictionaries[i].GetArrayLength())
                    throw new InvalidDataException("Invalid compact dictionary reference.");
                if (reference != -1) item[columns[i]] = JsonNode.Parse(dictionaries[i][reference].GetRawText());
            }
            events.Add(item);
        }
        output[feed.GetProperty("eventKey").GetString()!] = events;
        return output;
    }

    public static async Task<global::ParcelJourney.Domain.ParcelJourney?> TryReadAsync(string root, string searchTerm, CancellationToken token)
    {
        var path = root;
        if (Directory.Exists(root))
        {
            // Never use user input as an unchecked file-system path.
            path = Directory.EnumerateFiles(root, "*.pj.json.gz").FirstOrDefault(p => Path.GetFileName(p) == searchTerm + ".pj.json.gz")
                ?? Path.Combine(root, "compact-real-10.json");
            if (!File.Exists(path))
                foreach (var candidate in Directory.EnumerateFiles(root, "*.pj.json.gz"))
                {
                    var match = await TryReadAsync(candidate, searchTerm, token);
                    if (match is not null) return match;
                }
        }
        if (!File.Exists(path)) return null;
        await using var file = File.OpenRead(path);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress, true) : file;
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: token);
        var rootNode = document.RootElement;
        var feeds = rootNode.TryGetProperty("parcels", out var parcels) ? parcels.EnumerateArray().ToArray() : new[] { rootNode };
        foreach (var feed in feeds)
        {
            token.ThrowIfCancellationRequested();
            var decoded = Decode(feed);
            var metadata = decoded.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var id = metadata.GetValueOrDefault("parcelId")?.GetValue<string>();
            var aliases = metadata.GetValueOrDefault("aliases")?.AsArray();
            if (!string.Equals(id, searchTerm, StringComparison.OrdinalIgnoreCase) &&
                !(aliases?.Any(a => string.Equals(a?.GetValue<string>(), searchTerm, StringComparison.OrdinalIgnoreCase)) ?? false)) continue;
            return RealDatasetJourneyReader.FromJson(decoded.ToJsonString(), searchTerm);
        }
        return null;
    }
}
