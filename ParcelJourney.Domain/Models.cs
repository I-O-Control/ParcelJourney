namespace ParcelJourney.Domain;

public enum JourneyEventStatus { Confirmed, Inferred, Unmapped, Conflict }

public sealed record JourneyQuery(string SearchTerm, string LogRootPath, IReadOnlyList<string> FilePatterns, bool ForceFullLogScan = true);

public sealed record JourneyEvidence(string SourceFile, int LineNumber, string Source, string Details);

public sealed record ParcelJourneyEvent(
    DateTime Timestamp,
    string EventType,
    string Phase,
    string Summary,
    string? LocationId,
    string? EquipmentId,
    JourneyEventStatus Status,
    IReadOnlyList<string> Identifiers,
    IReadOnlyList<JourneyEvidence> Evidence);

public sealed record ParcelJourney(
    string SearchTerm,
    string? ParcelId,
    string? OrderId,
    string? TrackingId,
    IReadOnlyList<ParcelJourneyEvent> Events,
    TimeSpan Duration,
    bool HasUnmappedEvents)
{
    public static ParcelJourney Empty(string searchTerm) => new(searchTerm, null, null, null, [], TimeSpan.Zero, false);
}

public sealed record JourneyBuildOptions(string? DefaultLocationId = null);
