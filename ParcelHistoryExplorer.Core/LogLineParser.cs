using System.Globalization;
using System.Text.RegularExpressions;

namespace ParcelHistoryExplorer.Core;

internal static partial class LogLineParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy.MM.dd HH:mm:ss.fff",
        "yyyy.MM.dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss,fff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "dd.MM.yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss.fff"
    ];

    [GeneratedRegex(@"^(?<timestamp>(?:\d{4}[-\.]\d{2}[-\.]\d{2}|\d{2}\.\d{2}\.\d{4})\s+\d{2}:\d{2}:\d{2}(?:[\,\.]\d{3})?)\s+-\s+(?<level>[^-]+?)\s+-\s+(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex StandardLineRegex();

    public static ParsedLogLine Parse(string line, string sourceFile, int lineNumber, DateTime fileTimestampUtc)
    {
        var match = StandardLineRegex().Match(line);
        if (match.Success)
        {
            var timestampText = match.Groups["timestamp"].Value;
            var parsedTimestamp = DateTime.TryParseExact(
                timestampText,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var timestamp)
                ? timestamp
                : fileTimestampUtc.ToLocalTime();

            var message = match.Groups["rest"].Value;

            return new ParsedLogLine(
                parsedTimestamp,
                match.Groups["level"].Value.Trim(),
                message,
                sourceFile,
                lineNumber,
                IdentifierExtractor.Extract(line));
        }

        return new ParsedLogLine(
            fileTimestampUtc.ToLocalTime(),
            "INFO",
            line,
            sourceFile,
            lineNumber,
            IdentifierExtractor.Extract(line));
    }

    internal sealed record ParsedLogLine(
        DateTime Timestamp,
        string Level,
        string Message,
        string SourceFile,
        int LineNumber,
        IReadOnlyList<string> Identifiers);
}
