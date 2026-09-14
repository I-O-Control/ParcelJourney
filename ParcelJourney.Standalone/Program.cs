using ParcelHistoryExplorer.Core;
using ParcelJourney.Core;
using ParcelJourney.Domain;
using System.Text.Json;

static void Usage() => Console.Error.WriteLine("Usage: ParcelJourney.Standalone --logs <folder> --id <parcel-or-order-id> [--pattern <glob>] [--full]\n" +
    "You can also pass a JSON config path: ParcelJourney.Standalone --config <file>.\n" +
    "The folder may contain any compatible rotated log set; no fixed Fiege filenames are required.");

if (args.Length == 0) { Usage(); return 2; }
var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var patterns = new List<string>();
var fullScan = false;
for (var i = 0; i < args.Length; i++) {
    if (args[i].Equals("--full", StringComparison.OrdinalIgnoreCase)) { fullScan = true; continue; }
    if (args[i].Equals("--pattern", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { patterns.Add(args[++i]); continue; }
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) { values[args[i][2..]] = args[++i]; continue; }
}
if (values.TryGetValue("config", out var configPath)) {
    if (!File.Exists(configPath)) { Console.Error.WriteLine($"Config not found: {configPath}"); return 2; }
    var config = JsonSerializer.Deserialize<ReplayConfig>(await File.ReadAllTextAsync(configPath)) ?? new();
    values["logs"] ??= config.Logs;
    values["id"] ??= config.Id;
    if (patterns.Count == 0) patterns.AddRange(config.Patterns ?? []);
    fullScan |= config.Full;
}
if (string.IsNullOrWhiteSpace(values.GetValueOrDefault("logs")) || string.IsNullOrWhiteSpace(values.GetValueOrDefault("id"))) { Usage(); return 2; }
var logRoot = Path.GetFullPath(values["logs"]!);
if (!Directory.Exists(logRoot)) { Console.Error.WriteLine($"Log folder not found: {logRoot}"); return 2; }
patterns = patterns.Count == 0 ? ["*.log"] : patterns;
IParcelJourneyBuilder builder = new ParcelJourneyBuilder(new LogHistoryQueryEngine());
var journey = await builder.BuildAsync(new JourneyQuery(values["id"]!, logRoot, patterns, ForceFullLogScan: fullScan));
Console.WriteLine(JsonSerializer.Serialize(journey, new JsonSerializerOptions { WriteIndented = true }));
return 0;

public sealed class ReplayConfig { public string? Logs { get; set; } public string? Id { get; set; } public string[]? Patterns { get; set; } public bool Full { get; set; } }
