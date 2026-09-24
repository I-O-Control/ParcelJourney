using System.Text;

namespace ParcelHistoryExplorer.Core;

public static class SearchTraceLog
{
    private static readonly object sLock = new();
    private static readonly bool sEnabled =
        string.Equals(Environment.GetEnvironmentVariable("PHE_ENABLE_SEARCH_TRACE"), "1", StringComparison.OrdinalIgnoreCase);

    public static string GetLogFilePath() => Path.Combine(AppContext.BaseDirectory, "search-trace.txt");

    public static void Info(string source, string message) => Write("INFO", source, message, null);

    public static void Error(string source, string message, Exception? exception = null) => Write("ERROR", source, message, exception);

    private static void Write(string level, string source, string message, Exception? exception)
    {
        if (!sEnabled)
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
            builder.Append('[').Append(level).Append("] ");
            builder.Append('[').Append(source).Append("] ");
            builder.AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (sLock)
            {
                File.AppendAllText(GetLogFilePath(), builder.AppendLine().ToString());
            }
        }
        catch
        {
        }
    }
}
