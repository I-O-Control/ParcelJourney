using ParcelHistoryExplorer.Core;
using ParcelJourney.Domain;

namespace ParcelJourney.Core;

public sealed class ParcelJourneyBuilder : IParcelJourneyBuilder
{
    private readonly LogHistoryQueryEngine _engine;

    public ParcelJourneyBuilder(LogHistoryQueryEngine engine) => _engine = engine;

    public async Task<global::ParcelJourney.Domain.ParcelJourney> BuildAsync(JourneyQuery query, JourneyBuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _engine.SearchAsync(new ParcelHistoryQuery(query.LogRootPath, query.SearchTerm, query.FilePatterns, ForceFullFileScan: query.ForceFullLogScan), cancellationToken);
        if (result.Events.Count == 0) return global::ParcelJourney.Domain.ParcelJourney.Empty(query.SearchTerm);

        var events = result.Events.OrderBy(e => e.Timestamp).Select(e => new ParcelJourneyEvent(
            e.Timestamp, MapEventType(e.Stage), e.Phase, e.Summary,
            e.ScannerName, e.ScannerId, JourneyEventStatus.Confirmed,
            e.Identifiers, [new JourneyEvidence(e.SourceFile, e.LineNumber, e.Source, e.Details)])).ToArray();

        var first = events[0].Timestamp;
        var last = events[^1].Timestamp;
        return new global::ParcelJourney.Domain.ParcelJourney(query.SearchTerm,
            events.Select(e => e.Identifiers.FirstOrDefault(i => i.Length == 10 && i.All(char.IsDigit))).FirstOrDefault(i => i is not null),
            events.Select(e => e.Identifiers.FirstOrDefault(i => i.Length >= 11 && i.All(char.IsDigit))).FirstOrDefault(i => i is not null),
            events.Select(e => e.Identifiers.FirstOrDefault(i => i.Any(char.IsLetter) && i.Length >= 12)).FirstOrDefault(i => i is not null),
            events, last - first, false);
    }

    private static string MapEventType(string stage) => stage switch
    {
        "First Appearance" => "ParcelEnteredSystem",
        "Last Appearance" => "ParcelLastSeen",
        "OnScannerData" or "SPS->MFR Scanner Daten" => "Scanned",
        "ScanBruttoWaage" => "Weighed",
        "ScanEtiEingang" => "LabelEntry",
        "SendFileTCP" => "LabelPrinted",
        "ScanEtiAusgang" => "LabelExit",
        "SendTaskToPlc" => "DivertCommanded",
        "OnPlcAcknowledge" or "Ausschleuse Quittung" => "DivertConfirmed",
        "OnPlcCloseAcknowledge" => "Closed",
        "SaveToDB" => "StatePersisted",
        _ => stage
    };
}
