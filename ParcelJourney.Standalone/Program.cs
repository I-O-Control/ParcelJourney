using ParcelHistoryExplorer.Core;
using ParcelJourney.Core;
using ParcelJourney.Domain;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ParcelJourney.Standalone <log-root> <parcel-or-order-id> [pattern ...]");
    return 2;
}

IParcelJourneyBuilder builder = new ParcelJourneyBuilder(new LogHistoryQueryEngine());
var fullScan = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
var patterns = args.Skip(2).Where(a => !string.Equals(a, "--full", StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty("*.log").ToArray();
var journey = await builder.BuildAsync(new JourneyQuery(args[1], args[0], patterns, ForceFullLogScan: fullScan));
Console.WriteLine(JsonSerializer.Serialize(journey, new JsonSerializerOptions { WriteIndented = true }));
return 0;
