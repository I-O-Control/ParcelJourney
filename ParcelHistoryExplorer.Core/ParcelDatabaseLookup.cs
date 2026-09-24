using System.Data;
using System.Data.Common;
using System.Diagnostics;
using MySqlConnector;

namespace ParcelHistoryExplorer.Core;

public static class ParcelDatabaseLookup
{
    private const string DefaultConfigDbPath = @"C:\IOC_MFR\FiegeU\Config\MFRServiceConfiguration.db";
    private const string DefaultMySqlConnectionString = "Data Source=localhost;Database=mfr_fiege_ungarn_db;User=mfr;Password=mfr_dba1;";
    private static readonly TimeSpan EndPadding = TimeSpan.FromHours(1);

    internal static async Task<ParcelDatabaseLookupResult> TryResolveWindowAsync(string normalizedIdentifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentifier))
        {
            SearchTraceLog.Info("DbLookup", "Skipped DB lookup because identifier is blank.");
            return ParcelDatabaseLookupResult.FromFailure("DB lookup skipped because identifier is blank.");
        }

        try
        {
            SearchTraceLog.Info("DbLookup", $"Starting DB lookup for identifier '{normalizedIdentifier}'.");
            var connectionString = await TryGetMySqlConnectionStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                SearchTraceLog.Info("DbLookup", "DB lookup skipped because no MySQL connection string could be resolved from the config DB.");
                return ParcelDatabaseLookupResult.FromFailure("DB lookup failed because no MySQL connection string was resolved from the config DB.");
            }

            await using var connection = new MySqlConnection(connectionString);
            connection.ConnectionString = connectionString;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            SearchTraceLog.Info("DbLookup", "Opened MySQL connection successfully.");

            var recordResult = await TryReadRecordAsync(connection, normalizedIdentifier, cancellationToken).ConfigureAwait(false);
            if (!recordResult.Success)
            {
                return ParcelDatabaseLookupResult.FromFailure(recordResult.Message);
            }

            if (recordResult.Record is null)
            {
                SearchTraceLog.Info("DbLookup", $"No tudata row found for identifier '{normalizedIdentifier}'.");
                return ParcelDatabaseLookupResult.FromNotFound($"No tudata row found for identifier '{normalizedIdentifier}'.");
            }

            var record = recordResult.Record;
            var createTimestamp = record.CreateTimestamp;
            var updateTimestamp = record.UpdateTimestamp;
            var startUtc = createTimestamp.Kind == DateTimeKind.Utc ? createTimestamp : createTimestamp.ToUniversalTime();
            var endUtc = updateTimestamp.Kind == DateTimeKind.Utc ? updateTimestamp : updateTimestamp.ToUniversalTime();

            var window = new ParcelDatabaseWindow(
                startUtc,
                endUtc + EndPadding,
                record.ParcelId,
                record.OrderId,
                record.TrackingId,
                record.Status);
            SearchTraceLog.Info(
                "DbLookup",
                $"tudata hit for '{normalizedIdentifier}': Parcel='{window.ParcelId}', Order='{window.OrderId}', Tracking='{window.TrackingId}', Status='{window.Status}', CreateUtc={window.StartUtc:O}, EndUtc={window.EndUtc:O}.");
            return ParcelDatabaseLookupResult.FromWindow(window);
        }
        catch (Exception ex)
        {
            SearchTraceLog.Error("DbLookup", $"DB lookup failed for identifier '{normalizedIdentifier}'.", ex);
            return ParcelDatabaseLookupResult.FromFailure($"DB lookup failed: {ex.Message}");
        }
    }

    public static async Task<ParcelDatabaseRecordLookupResult> TryGetCurrentRecordAsync(string normalizedIdentifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentifier))
        {
            return ParcelDatabaseRecordLookupResult.FromFailure("DB lookup skipped because identifier is blank.");
        }

        try
        {
            var connectionString = await TryGetMySqlConnectionStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return ParcelDatabaseRecordLookupResult.FromFailure("DB lookup failed because no MySQL connection string was resolved.");
            }

            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            SearchTraceLog.Info("DbLookup", $"Opened MySQL connection for database detail lookup of '{normalizedIdentifier}'.");
            return await TryReadRecordAsync(connection, normalizedIdentifier, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SearchTraceLog.Error("DbLookup", $"Database detail lookup failed for identifier '{normalizedIdentifier}'.", ex);
            return ParcelDatabaseRecordLookupResult.FromFailure($"Database detail lookup failed: {ex.Message}");
        }
    }

    private static async Task<string?> TryGetMySqlConnectionStringAsync(CancellationToken cancellationToken)
    {
        var directConnectionString = Environment.GetEnvironmentVariable("PHE_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(directConnectionString))
        {
            directConnectionString = DefaultMySqlConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(directConnectionString))
        {
            SearchTraceLog.Info("DbLookup", "Using direct MySQL connection string.");
            return directConnectionString;
        }

        var configPath = ResolveConfigDbPath();

        if (!File.Exists(configPath))
        {
            SearchTraceLog.Info("DbLookup", $"Config DB not found at '{configPath}'.");
            return null;
        }

        var sqlitePath = ResolveSqlitePath();
        if (sqlitePath is null)
        {
            SearchTraceLog.Info("DbLookup", "sqlite3.exe was not found. Checked PHE_SQLITE3_PATH, app folder, PATH, and common install locations.");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sqlitePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add(
            """
            SELECT ConnectionString
            FROM connectionstrings
            WHERE Name IN ('Life', 'Default', 'Debug')
            ORDER BY CASE Name
                WHEN 'Life' THEN 0
                WHEN 'Default' THEN 1
                WHEN 'Debug' THEN 2
                ELSE 3
            END
            LIMIT 1;
            """);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            SearchTraceLog.Error("DbLookup", $"sqlite3 exited with code {process.ExitCode} while reading '{configPath}'. Error: {error}");
            return null;
        }

        var connectionString = string.IsNullOrWhiteSpace(output) ? null : output;
        SearchTraceLog.Info(
            "DbLookup",
            string.IsNullOrWhiteSpace(connectionString)
                ? "Config DB did not return a MySQL connection string."
                : "Resolved MySQL connection string from config DB.");
        return connectionString;
    }

    private static string ResolveConfigDbPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("PHE_MFR_CONFIG_DB");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return DefaultConfigDbPath;
    }

    private static string? ResolveSqlitePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("PHE_SQLITE3_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var appLocalPath = Path.Combine(AppContext.BaseDirectory, "sqlite3.exe");
        if (File.Exists(appLocalPath))
        {
            return appLocalPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var wingetPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "sqlite3.exe");
            if (File.Exists(wingetPath))
            {
                return wingetPath;
            }
        }

        var commonCandidates = new[]
        {
            @"C:\sqlite3\sqlite3.exe",
            @"C:\Program Files\SQLite\sqlite3.exe",
            @"C:\Program Files (x86)\SQLite\sqlite3.exe"
        };

        foreach (var candidate in commonCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment, "sqlite3.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? ReadNullableString(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static async Task<ParcelDatabaseRecordLookupResult> TryReadRecordAsync(MySqlConnection connection, string normalizedIdentifier, CancellationToken cancellationToken)
    {
        var fallbackIdentifier = IdentifierExtractor.Normalize(normalizedIdentifier);
        var combinedVariants = GetCombinedZpVariants(fallbackIdentifier);

        await using var command = new MySqlCommand
        {
            Connection = connection,
            CommandText =
                """
                SELECT RowId, PrincipalId, OrderId, ParcelId, ControlFlag, GrossWeight, CarrierId, CartonType, LastScanPos,
                       WeightStart, WeightBrutto, VRNewHeight, PlcTarget, TrackingId, Status, CreateTimestamp, UpdateTimestamp
                FROM tudata
                WHERE ParcelId = @term OR OrderId = @term OR TrackingId = @term
                   OR (@fallbackTerm <> @term AND (ParcelId = @fallbackTerm OR OrderId = @fallbackTerm OR TrackingId = @fallbackTerm))
                   OR ((@zpOrderA <> '' AND @zpParcelA <> '') AND ((OrderId = @zpOrderA AND ParcelId = @zpParcelA) OR (OrderId = @zpParcelA AND ParcelId = @zpOrderA)))
                   OR ((@zpOrderB <> '' AND @zpParcelB <> '') AND ((OrderId = @zpOrderB AND ParcelId = @zpParcelB) OR (OrderId = @zpParcelB AND ParcelId = @zpOrderB)))
                ORDER BY UpdateTimestamp DESC
                LIMIT 1;
                """
        };

        command.Parameters.AddWithValue("@term", normalizedIdentifier);
        command.Parameters.AddWithValue("@fallbackTerm", fallbackIdentifier);
        command.Parameters.AddWithValue("@zpOrderA", combinedVariants.OrderFirst);
        command.Parameters.AddWithValue("@zpParcelA", combinedVariants.ParcelSecond);
        command.Parameters.AddWithValue("@zpOrderB", combinedVariants.ParcelFirst);
        command.Parameters.AddWithValue("@zpParcelB", combinedVariants.OrderSecond);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ParcelDatabaseRecordLookupResult.FromNotFound($"No tudata row found for identifier '{normalizedIdentifier}'.");
        }

        return ParcelDatabaseRecordLookupResult.FromRecord(
            new ParcelDatabaseRecord(
                ReadObject(reader, "RowId"),
                ReadNullableString(reader, "PrincipalId"),
                ReadNullableString(reader, "OrderId"),
                ReadNullableString(reader, "ParcelId"),
                ReadNullableString(reader, "ControlFlag"),
                ReadObject(reader, "GrossWeight"),
                ReadNullableString(reader, "CarrierId"),
                ReadNullableString(reader, "CartonType"),
                ReadNullableString(reader, "LastScanPos"),
                ReadObject(reader, "WeightStart"),
                ReadObject(reader, "WeightBrutto"),
                ReadObject(reader, "VRNewHeight"),
                ReadNullableString(reader, "PlcTarget"),
                ReadNullableString(reader, "TrackingId"),
                ReadNullableString(reader, "Status"),
                reader.GetDateTime(reader.GetOrdinal("CreateTimestamp")),
                reader.GetDateTime(reader.GetOrdinal("UpdateTimestamp"))));
    }

    private static CombinedZpVariants GetCombinedZpVariants(string normalizedIdentifier)
    {
        if (normalizedIdentifier.Length != 20 || !normalizedIdentifier.All(char.IsDigit))
        {
            return CombinedZpVariants.Empty;
        }

        var firstHalf = normalizedIdentifier[..10];
        var secondHalf = normalizedIdentifier[10..];
        return new CombinedZpVariants(firstHalf, secondHalf, secondHalf, firstHalf);
    }

    private static object? ReadObject(DbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
    }

    private sealed record CombinedZpVariants(string OrderFirst, string ParcelSecond, string ParcelFirst, string OrderSecond)
    {
        public static CombinedZpVariants Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    }
}

internal sealed record ParcelDatabaseWindow(
    DateTime StartUtc,
    DateTime EndUtc,
    string? ParcelId,
    string? OrderId,
    string? TrackingId,
    string? Status);

internal sealed record ParcelDatabaseLookupResult(
    bool Success,
    bool Found,
    string Message,
    ParcelDatabaseWindow? Window)
{
    public static ParcelDatabaseLookupResult FromWindow(ParcelDatabaseWindow window) => new(true, true, "DB lookup found a tudata row.", window);

    public static ParcelDatabaseLookupResult FromNotFound(string message) => new(true, false, message, null);

    public static ParcelDatabaseLookupResult FromFailure(string message) => new(false, false, message, null);
}

public sealed record ParcelDatabaseRecord(
    object? RowId,
    string? PrincipalId,
    string? OrderId,
    string? ParcelId,
    string? ControlFlag,
    object? GrossWeight,
    string? CarrierId,
    string? CartonType,
    string? LastScanPos,
    object? WeightStart,
    object? WeightBrutto,
    object? VrNewHeight,
    string? PlcTarget,
    string? TrackingId,
    string? Status,
    DateTime CreateTimestamp,
    DateTime UpdateTimestamp);

public sealed record ParcelDatabaseRecordLookupResult(
    bool Success,
    bool Found,
    string Message,
    ParcelDatabaseRecord? Record)
{
    public static ParcelDatabaseRecordLookupResult FromRecord(ParcelDatabaseRecord record) => new(true, true, "Database record found.", record);

    public static ParcelDatabaseRecordLookupResult FromNotFound(string message) => new(true, false, message, null);

    public static ParcelDatabaseRecordLookupResult FromFailure(string message) => new(false, false, message, null);
}
