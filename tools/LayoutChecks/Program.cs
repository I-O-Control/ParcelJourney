using System.Text.Json;
using System.Text.Json.Nodes;
using ParcelJourney.App;

var directory = Path.Combine(Path.GetTempPath(), "parcel-layout-checks-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("PARCELJOURNEY_LAYOUT_PATH", Path.Combine(directory, "layout.json"));
var source = """
{"nodes":[{"id":"A","label":"A","x":0,"y":0},{"id":"B","label":"B","x":100,"y":100}],"edges":[{"id":"AB","a":"A","b":"B","points":[[0,0],[100,100]]}],"parcels":[{"events":[{"raw":"original evidence"}],"stops":[{"id":"A"},{"id":"B"}],"legs":[{"a":"A","b":"B","start":0,"end":10,"edges":["AB"]}]}]}
""";
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidOperationException || e is InvalidDataException) { Check(true, name); return; } throw new Exception("Not rejected: " + name); }
JsonObject Read() => JsonNode.Parse(JsonSerializer.Serialize(LayoutStore.Get(source)))!.AsObject();
try
{
    var state = Read(); var layout = state["layout"]!.AsObject(); var revision = state["revision"]!.GetValue<string>();
    Check(LayoutStore.Apply(source) == source, "No saved layout preserves source");
    layout["nodes"]![0]!["x"] = -50; layout["edges"]![0]!["points"]![0]![0] = -50;
    revision = LayoutStore.Save(layout, revision, source);
    var projected = JsonNode.Parse(LayoutStore.Apply(source))!;
    Check(projected["nodes"]![0]!["x"]!.GetValue<int>() == -50, "Move survives save and reload");
    var originalParcel = JsonNode.Parse(source)!["parcels"]![0]!;
    var projectedParcel = projected["parcels"]![0]!;
    string LegEvidence(JsonNode? leg) => string.Join("|", new[] { "a", "b", "start", "end" }.Select(key => leg![key]?.ToJsonString()));
    Check(projectedParcel["events"]!.ToJsonString() == originalParcel["events"]!.ToJsonString()
        && projectedParcel["stops"]!.ToJsonString() == originalParcel["stops"]!.ToJsonString()
        && projectedParcel["legs"]!.AsArray().Select(LegEvidence).SequenceEqual(originalParcel["legs"]!.AsArray().Select(LegEvidence)),
        "Geometry edit preserves captured events, scanner sequence and timings while route annotations can be recomputed");
    Reject(() => LayoutStore.Save(layout, "stale", source), "Concurrent editor cannot overwrite");
    var broken = layout.DeepClone().AsObject(); broken["nodes"]!.AsArray().RemoveAt(1);
    Reject(() => LayoutStore.Save(broken, revision, source), "Dangling endpoint rejected");
    broken = layout.DeepClone().AsObject(); broken["edges"]![0]!["points"]![0]![0] = 99;
    Reject(() => LayoutStore.Save(broken, revision, source), "Detached endpoint rejected");
    layout["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"J","label":"Junction J","x":100,"y":0} """));
    layout["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"K","label":"Junction K","x":0,"y":100} """));
    layout["edges"] = JsonNode.Parse("""[{"id":"AJ","a":"A","b":"J","points":[[-50,0],[100,0]],"userDefined":true},{"id":"JB","a":"J","b":"B","points":[[100,0],[100,100]],"userDefined":true},{"id":"AK","a":"A","b":"K","points":[[-50,0],[0,100]],"userDefined":true},{"id":"KB","a":"K","b":"B","points":[[0,100],[100,100]],"userDefined":true}]""");
    revision = LayoutStore.Save(layout, revision, source);
    projected = JsonNode.Parse(LayoutStore.Apply(source))!;
    Check(projected["parcels"]![0]!["legs"]![0]!["edges"]!.AsArray().Count == 0, "Deleted route stays explicitly unresolved instead of inventing a shortest path");
    Check(projected["parcels"]![0]!["legs"]![0]!["routeStatus"]!.GetValue<string>() == "unknown", "Deleted route is marked unknown for replay");
    Check(projected["parcels"]![0]!["events"]!.ToJsonString().Contains("original evidence"), "An unresolved route preserves raw evidence");
    layout["edges"]!.AsArray().RemoveAt(3); layout["edges"]!.AsArray().RemoveAt(2);
    revision = LayoutStore.Save(layout, revision, source);
    projected = JsonNode.Parse(LayoutStore.Apply(source))!;
    Check(projected["parcels"]![0]!["legs"]![0]!["routeStatus"]!.GetValue<string>() == "user-defined-path", "A unique explicitly drawn path through a junction resolves the recorded leg");
    Check(projected["parcels"]![0]!["legs"]![0]!["edges"]!.ToJsonString() == "[\"AJ\",\"JB\"]", "Replay follows the user-drawn belt sequence exactly");
    layout["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"USER_AB","a":"A","b":"B","points":[[-50,0],[100,100]],"routeType":"trunk","userDefined":true}"""));
    revision = LayoutStore.Save(layout, revision, source);
    projected = JsonNode.Parse(LayoutStore.Apply(source))!;
    Check(projected["parcels"]![0]!["legs"]![0]!["edges"]![0]!.GetValue<string>() == "USER_AB", "An explicit user belt between observed scanners resolves only that exact leg");
    broken = layout.DeepClone().AsObject(); broken["edges"]!.AsArray().RemoveAt(2); broken["edges"]!.AsArray().RemoveAt(1);
    revision = LayoutStore.Save(broken, revision, source);
    Check(Read()["revision"]!.GetValue<string>() == revision, "An intentionally disconnected belt layout can be saved");
    var other = JsonNode.Parse(source)!; other["parcels"]![0]!["stops"]![0]!["id"] = "MISSING";
    var incompatible = JsonNode.Parse(LayoutStore.Apply(other.ToJsonString()))!;
    Check(incompatible["layoutWarning"] is not null, "Incompatible capture reports fallback explicitly");
    Check(incompatible["nodes"]!.ToJsonString() == other["nodes"]!.ToJsonString(), "Incompatible capture remains playable with source geometry");
    var sentinel = JsonNode.Parse(source)!;
    sentinel["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"UNOBSERVED_POSITION","label":"Unknown","x":2400,"y":2600,"physical":false}"""));
    sentinel["parcels"]![0]!["stops"]!.AsArray().Insert(0, JsonNode.Parse("""{"id":"UNOBSERVED_POSITION"}"""));
    var projectedSentinel = JsonNode.Parse(LayoutStore.Apply(sentinel.ToJsonString()))!;
    Check(projectedSentinel["layoutWarning"] is null, "Later captures retain their nonphysical unknown-position sentinel");
    Check(projectedSentinel["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "UNOBSERVED_POSITION")!["physical"]!.GetValue<bool>() == false, "Unknown-position sentinel remains explicitly nonphysical in the 3D model");
    layout["edges"]![0]!["evidence"] = "private capture record";
    LayoutStore.Save(layout, revision, source);
    Check(!File.ReadAllText(LayoutStore.FilePath).Contains("private capture record"), "Layout file excludes captured edge evidence");
    var corridorEdges = JsonNode.Parse("""[{"id":"AP","a":"SC_A","b":"PRINT","topology":"floor-layout"},{"id":"PB","a":"PRINT","b":"SC_B","topology":"floor-layout"}]""")!.AsArray();
    Check(CorridorRoutes.Find("SC_A", "SC_B", corridorEdges)?.Count == 2, "A continuous modeled corridor through unscanned equipment resolves");
    corridorEdges[0]!["b"] = "SC_OTHER"; corridorEdges[1]!["a"] = "SC_OTHER";
    Check(CorridorRoutes.Find("SC_A", "SC_B", corridorEdges) is null, "An arbitrary detour through another scanner does not resolve missing geometry");
    corridorEdges[0]!["b"] = "PRINT"; corridorEdges[1]!["a"] = "PRINT";
    corridorEdges.Add(JsonNode.Parse("""{"id":"AX","a":"SC_A","b":"SEAL1","topology":"floor-layout"}"""));
    corridorEdges.Add(JsonNode.Parse("""{"id":"XB","a":"SEAL1","b":"SC_B","topology":"floor-layout"}"""));
    Check(CorridorRoutes.Find("SC_A", "SC_B", corridorEdges) is null, "Ambiguous branches remain unresolved");
    Check(CorridorRoutes.Find("SC_A", "SC_B", corridorEdges, "SEAL1")?.First()["id"]!.ToString() == "AX", "A confirmed branch selects its connected corridor");
    var readers = JsonNode.Parse("""{"nodes":[{"id":"SC_ETIOSL4","x":10,"y":20},{"id":"SC_ETIOTL4","x":10,"y":20}],"edges":[],"parcels":[{"events":[],"legs":[{"a":"SC_ETIOSL4","b":"SC_ETIOTL4","captureRouteStatus":"co-located-read-head-change","edges":[]}]}]}""")!.AsObject();
    CorridorRoutes.Resolve(readers);
    Check(readers["parcels"]![0]!["legs"]![0]!["routeStatus"]!.ToString() == "co-located-read-head-change", "Co-located side/top readers preserve known position without inventing a belt");
    readers["nodes"]![1]!["x"] = 50;
    CorridorRoutes.Resolve(readers);
    Check(readers["parcels"]![0]!["legs"]![0]!["routeStatus"]!.ToString() == "unknown", "Moving a read head apart keeps its physical transfer unresolved");
    Console.WriteLine($"{passed} layout checks passed.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
