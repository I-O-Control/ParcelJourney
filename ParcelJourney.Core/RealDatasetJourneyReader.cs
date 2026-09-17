using System.Globalization;
using System.Text.Json;
using ParcelJourney.Domain;

namespace ParcelJourney.Core;

/// <summary>Reads the normalized, real-log parcel feed produced by the middleware.</summary>
internal static class RealDatasetJourneyReader
{
    private static readonly string[] TimestampFormats = ["yyyy.MM.dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fff", "O"];

    public static async Task<global::ParcelJourney.Domain.ParcelJourney?> TryReadAsync(string root, string searchTerm, CancellationToken cancellationToken)
    {
        var parcelRoot = Directory.Exists(Path.Combine(root, "parcels")) ? Path.Combine(root, "parcels") : root;
        if (!Directory.Exists(parcelRoot)) return null;

        var direct = Path.Combine(parcelRoot, searchTerm + ".json");
        string? path = File.Exists(direct) ? direct : null;
        if (!File.Exists(direct))
        {
            foreach (var candidate in Directory.EnumerateFiles(parcelRoot, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = File.OpenRead(candidate);
                var probe = await JsonSerializer.DeserializeAsync<RealParcel>(stream, Options, cancellationToken);
                if (probe is not null && (string.Equals(probe.ParcelId, searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    (probe.Aliases?.Any(a => string.Equals(a, searchTerm, StringComparison.OrdinalIgnoreCase)) ?? false))) { path = candidate; break; }
            }
        }
        if (path is null) return null;

        await using var input = File.OpenRead(path);
        var parcel = await JsonSerializer.DeserializeAsync<RealParcel>(input, Options, cancellationToken);
        return MapParcel(parcel, searchTerm);
    }

    internal static global::ParcelJourney.Domain.ParcelJourney? FromJson(string json, string searchTerm) => MapParcel(JsonSerializer.Deserialize<RealParcel>(json, Options), searchTerm);

    private static global::ParcelJourney.Domain.ParcelJourney? MapParcel(RealParcel? parcel, string searchTerm)
    {
        if (parcel?.Events is null || parcel.Events.Count == 0) return null;

        var events = parcel.Events.Select(e => MapEvent(e, parcel.Aliases ?? [], parcel.ParcelId)).OrderBy(e => e.Timestamp).ToArray();
        var first = events[0].Timestamp;
        var last = events[^1].Timestamp;
        var ids = (parcel.Aliases ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        return new global::ParcelJourney.Domain.ParcelJourney(searchTerm,
            parcel.ParcelId ?? ids.FirstOrDefault(i => i.Length == 10 && i.All(char.IsDigit)),
            ids.FirstOrDefault(i => i.Length >= 11 && i.All(char.IsDigit)),
            ids.FirstOrDefault(i => i.Any(char.IsLetter) && i.Length >= 12),
            events, last - first, events.Any(e => e.Status == JourneyEventStatus.Unmapped));
    }

    private static ParcelJourneyEvent MapEvent(RealEvent e, IReadOnlyList<string> aliases, string? parcelId)
    {
        var raw = e.Raw ?? "";
        var type = raw.Contains("OnClose", StringComparison.OrdinalIgnoreCase) || raw.Contains("CLOSED", StringComparison.OrdinalIgnoreCase) ? "Closed" :
            raw.Contains("OnScannerData", StringComparison.OrdinalIgnoreCase) ? "Scanned" :
            raw.Contains("ScanBruttoWaage", StringComparison.OrdinalIgnoreCase) || raw.Contains("WEIGHTOK", StringComparison.OrdinalIgnoreCase) ? "Weighed" :
            raw.Contains("SendTaskToPlc", StringComparison.OrdinalIgnoreCase) || raw.Contains("Fahrziel", StringComparison.OrdinalIgnoreCase) ? "DivertCommanded" :
            raw.Contains("Ausschleuse Quittung", StringComparison.OrdinalIgnoreCase) || raw.Contains("Acknowledge", StringComparison.OrdinalIgnoreCase) ? "DivertConfirmed" :
            raw.Contains("SaveToDB", StringComparison.OrdinalIgnoreCase) ? "StatePersisted" : e.Layer ?? "Observed";
        var timestamp = ParseTimestamp(e.Timestamp);
        var ids = aliases.Concat(ExtractTokens(raw)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var layer = NormalizeLayer(e.Layer, e.SourceFile, raw);
        var evidence = new JourneyEvidence(e.SourceFile ?? "", e.Line, layer, raw);
        return new ParcelJourneyEvent(timestamp, type, layer, raw, e.Location ?? InferLocation(raw), null,
            JourneyEventStatus.Confirmed, ids, [evidence]);
    }

    private static string NormalizeLayer(string? layer, string? sourceFile, string raw)
    {
        var name = Path.GetFileName(sourceFile ?? "");
        if (name.StartsWith("ProcCamera", StringComparison.OrdinalIgnoreCase) || raw.Contains("OnSendInfoStringToCamera", StringComparison.OrdinalIgnoreCase)) return "Camera";
        if (name.StartsWith("RemoteManagement", StringComparison.OrdinalIgnoreCase) || raw.Contains("Sending Waage data to UI", StringComparison.OrdinalIgnoreCase)) return "UI";
        if (name.StartsWith("ProcEtikettierer", StringComparison.OrdinalIgnoreCase) || raw.Contains("OnDateiDrucken", StringComparison.OrdinalIgnoreCase) || raw.Contains("DoZplPrintJob", StringComparison.OrdinalIgnoreCase) || raw.Contains("UpdateTrackingId", StringComparison.OrdinalIgnoreCase)) return "Labeler";
        return string.Equals(layer, "Other", StringComparison.OrdinalIgnoreCase) ? "Other" : (layer ?? "Observed");
    }

    private static DateTime ParseTimestamp(string? value) =>
        DateTime.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result : DateTime.MinValue;

    private static string? InferLocation(string raw)
    {
        var marker = raw.IndexOf("SC_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var end = marker;
        while (end < raw.Length && (char.IsLetterOrDigit(raw[end]) || raw[end] == '_')) end++;
        return raw[marker..end];
    }

    private static IEnumerable<string> ExtractTokens(string raw)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, @"\b\d{10,26}\b")) yield return m.Value;
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private sealed class RealParcel { public string? ParcelId { get; set; } public string[]? Aliases { get; set; } public List<RealEvent>? Events { get; set; } }
    private sealed class RealEvent { public string? Timestamp { get; set; } public string? SourceFile { get; set; } public int Line { get; set; } public string? Raw { get; set; } public string? Layer { get; set; } public string? Location { get; set; } }
}
