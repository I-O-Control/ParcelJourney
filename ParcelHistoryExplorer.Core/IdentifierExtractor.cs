using System.Text.RegularExpressions;

namespace ParcelHistoryExplorer.Core;

internal static partial class IdentifierExtractor
{
    private static readonly string[] KnownKeys =
    [
        "parcelid",
        "parcel",
        "zp",
        "trackingid",
        "tracking",
        "barcode",
        "sscc",
        "shipment",
        "sendung",
        "paket"
    ];

    [GeneratedRegex(@"(?<key>ParcelID|ParcelId|Parcel|ZP|TrackingID|TrackingId|Tracking|Barcode|SSCC|Shipment|Sendung|Paket)\s*[:=]\s*(?<value>[A-Za-z0-9/_\-\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"\bZP[\s_]+(?<value>\d{8,30})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ZpValueRegex();

    [GeneratedRegex(@"\b1Z[A-Z0-9]{8,24}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UpsTrackingRegex();

    [GeneratedRegex(@"\b\d{10,30}\b", RegexOptions.Compiled)]
    private static partial Regex LongDigitsRegex();

    [GeneratedRegex(@"\b(?<value>[A-Z0-9][A-Z0-9/_\-]{5,30})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    public static IReadOnlyList<string> Extract(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Array.Empty<string>();
        }

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in KeyValueRegex().Matches(line))
        {
            var value = Normalize(match.Groups["value"].Value);
            if (!string.IsNullOrEmpty(value))
            {
                identifiers.Add(value);
            }
        }

        foreach (Match match in ZpValueRegex().Matches(line))
        {
            identifiers.Add(Normalize($"ZP{match.Groups["value"].Value}"));
            identifiers.Add(Normalize(match.Groups["value"].Value));
        }

        foreach (Match match in UpsTrackingRegex().Matches(line))
        {
            identifiers.Add(Normalize(match.Value));
        }

        foreach (Match match in LongDigitsRegex().Matches(line))
        {
            identifiers.Add(match.Value);
        }

        foreach (Match match in TokenRegex().Matches(line))
        {
            var candidate = match.Groups["value"].Value;
            if (LooksLikeInterestingIdentifier(candidate))
            {
                identifiers.Add(Normalize(candidate));
            }
        }

        return identifiers.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToUpperInvariant(ch);
            }
        }

        return new string(buffer[..length]);
    }

    public static string NormalizePreservingTrackingChars(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '%' or '/' or '_' or '-' or '.')
            {
                buffer[length++] = char.ToUpperInvariant(ch);
            }
        }

        return new string(buffer[..length]);
    }

    public static IReadOnlyList<string> ExpandSearchTokens(string value)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = Normalize(value);
        var preserved = NormalizePreservingTrackingChars(value);

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            expanded.Add(normalized);
            AddCombinedZpSegments(expanded, normalized);
        }

        if (!string.IsNullOrWhiteSpace(preserved))
        {
            expanded.Add(preserved);
        }

        foreach (var token in Extract(value))
        {
            expanded.Add(token);
            AddCombinedZpSegments(expanded, token);
        }

        return expanded.ToArray();
    }

    public static string NormalizeSearchInput(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var preserved = NormalizePreservingTrackingChars(trimmed);
        return LooksLikeRawTrackingIdentifier(preserved)
            ? preserved
            : Normalize(trimmed);
    }

    public static bool TryExtractCombinedZpParts(string? value, out string orderId, out string parcelId)
    {
        var normalized = Normalize(value ?? string.Empty);
        return TrySplitCombinedZp(normalized, out orderId, out parcelId);
    }

    public static bool IsUsefulParcelIdentity(string? value)
    {
        var normalized = Normalize(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized is "NOREAD" or "NOVALIDTRACKINGIDPROVIDED")
        {
            return false;
        }

        if (normalized.All(static ch => ch == '9') && normalized.Length >= 8)
        {
            return false;
        }

        if (normalized.Length == 10 && normalized.All(char.IsDigit))
        {
            return true;
        }

        if (normalized.Length > 10 && normalized.All(char.IsDigit))
        {
            return true;
        }

        if (normalized.StartsWith("1Z", StringComparison.OrdinalIgnoreCase) && normalized.Length >= 10)
        {
            return true;
        }

        return normalized.Length >= 8 && normalized.Any(char.IsDigit) && normalized.Any(char.IsLetter);
    }

    public static bool LooksLikeRawTrackingIdentifier(string? value)
    {
        var preserved = NormalizePreservingTrackingChars(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(preserved))
        {
            return false;
        }

        if (preserved.Length <= 10)
        {
            return false;
        }

        if (preserved.Contains('%') || preserved.Contains('/') || preserved.Contains('-') || preserved.Contains('_') || preserved.Contains('.'))
        {
            return preserved.Any(char.IsDigit);
        }

        if (preserved.All(char.IsDigit))
        {
            return preserved.Length >= 22;
        }

        return preserved.Length >= 14 && preserved.Any(char.IsLetter) && preserved.Any(char.IsDigit);
    }

    private static bool LooksLikeInterestingIdentifier(string candidate)
    {
        var normalized = Normalize(candidate);
        if (normalized.Length < 6)
        {
            return false;
        }

        if (KnownKeys.Any(key => normalized.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return normalized.Any(char.IsDigit) && normalized.Any(char.IsLetter);
    }

    private static void AddCombinedZpSegments(HashSet<string> tokens, string token)
    {
        if (!TrySplitCombinedZp(token, out var orderId, out var parcelId))
        {
            return;
        }

        tokens.Add(orderId);
        tokens.Add(parcelId);
    }

    private static bool TrySplitCombinedZp(string token, out string orderId, out string parcelId)
    {
        orderId = string.Empty;
        parcelId = string.Empty;

        if (token.Length != 20 || !token.All(char.IsDigit) || !token.StartsWith("00", StringComparison.Ordinal))
        {
            return false;
        }

        orderId = token[..10];
        parcelId = token[10..];

        if (!orderId.StartsWith("00", StringComparison.Ordinal) || !parcelId.StartsWith("00", StringComparison.Ordinal))
        {
            orderId = string.Empty;
            parcelId = string.Empty;
            return false;
        }

        return true;
    }
}
