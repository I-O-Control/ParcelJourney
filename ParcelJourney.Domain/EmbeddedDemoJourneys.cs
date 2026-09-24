namespace ParcelJourney.Domain;

public enum DemoJourneyKind { Synthetic, RecordedSample }

public sealed record EmbeddedDemoJourney(string Id, DemoJourneyKind Kind, ParcelJourney Journey);

/// <summary>Small, self-contained examples used when the host has no log folder configured.</summary>
public static class EmbeddedDemoJourneys
{
    public static IReadOnlyList<EmbeddedDemoJourney> All { get; } = Build();

    private static IReadOnlyList<EmbeddedDemoJourney> Build()
    {
        var result = new List<EmbeddedDemoJourney>();
        for (var i = 1; i <= 5; i++)
            result.Add(new($"synthetic-{i:00}", DemoJourneyKind.Synthetic, Create($"DEMO-S-{i:00}", false, i)));
        for (var i = 1; i <= 5; i++)
            result.Add(new($"recorded-{i:00}", DemoJourneyKind.RecordedSample, Create($"DEMO-R-{i:00}", true, i)));
        return result;
    }

    private static ParcelJourney Create(string id, bool recorded, int variant)
    {
        var start = new DateTime(2026, 9, 24, 9, variant, 0, DateTimeKind.Utc);
        var locations = recorded
            ? variant switch
            {
                1 => new[] { "SC_START", "SC_WAAP", "SC_WAVL", "SC_VL1", "SC_BWL3", "SC_ETIINL3", "SC_ETIOSL3", "SC_ETIOTL3", "SC_H4T1", "SC_H4T4", "SC_H4RU", "SC_H3RU", "SC_H3T2", "SC_H2T1" },
                2 => new[] { "SC_START", "SC_WAAP", "SC_AP", "SC_WAVL", "SC_VL2", "SC_BWL5", "SC_ETIINL5", "SC_ETIOSL5", "SC_ETIOTL5", "SC_H4T1", "SC_H4T8", "SC_H4RU", "SC_H3RU", "SC_H3T6" },
                3 => new[] { "SC_START", "SC_WAAP", "SC_UMR", "SC_WAVL", "SC_VL1", "SC_BWL4", "SC_ETIINL4", "SC_ETIOSL4", "SC_ETIOTL4", "SC_H4T2", "SC_H4RU", "SC_H3RU", "SC_H3T1", "SC_H2T9" },
                4 => new[] { "SC_START", "SC_WAAP", "SC_WAVL", "SC_VL1", "SC_VL2", "SC_BWL3", "SC_ETIINL3", "SC_ETIOSL3", "SC_ETIOTL3", "SC_H4T1", "SC_H4RU", "SC_H3RU", "SC_H3T7", "SC_H2T4" },
                _ => new[] { "SC_START", "SC_WAAP", "SC_AP", "SC_UMR", "SC_WAVL", "SC_VL2", "SC_BWL5", "SC_ETIINL5", "SC_ETIOSL5", "SC_ETIOTL5", "SC_H4T3", "SC_H4RU", "SC_H3RU", "SC_H2T9", "SC_H4T1" }
            }
            : variant switch
            {
                1 => new[] { "ENTRY", "WORKSTATION", "SEALER_SELECT", "SEALER_2", "VERIFY", "HALL_A", "CHUTE_A" },
                2 => new[] { "ENTRY", "WORKSTATION", "QUALITY_CHECK", "SEALER_SELECT", "SEALER_5", "VERIFY", "HALL_B", "CHUTE_B" },
                3 => new[] { "ENTRY", "WORKSTATION", "SEALER_SELECT", "SEALER_1", "VERIFY", "HALL_A", "RECIRCULATION", "CHUTE_C" },
                4 => new[] { "ENTRY", "MANUAL_CLOSE", "SEALER_SELECT", "SEALER_7", "VERIFY", "HALL_C", "QUALITY_EXIT" },
                _ => new[] { "ENTRY", "WORKSTATION", "SEALER_SELECT", "NO_READ", "MANUAL_REVIEW", "RELEASE" }
            };

        var events = locations.Select((location, index) => new ParcelJourneyEvent(
            start.AddSeconds(index * (recorded ? 6 : 3)),
            index == 0 ? "Entry" : index == locations.Length - 1 ? "Exit" : "Routing",
            index == 0 ? "Start" : index == locations.Length - 1 ? "Completed" : "Transit",
            $"{(recorded ? "Recorded" : "Synthetic")} demo observation at {location}",
            location, null, JourneyEventStatus.Confirmed, [id], [], "embedded-demo", null, "demo", "Recorded")).ToList();

        return new ParcelJourney(id, id, $"ORDER-{id}", null, events, TimeSpan.FromSeconds((locations.Length - 1) * (recorded ? 6 : 3)), false);
    }
}
