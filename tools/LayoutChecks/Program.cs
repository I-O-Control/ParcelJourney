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
    Check(projected["parcels"]!.ToJsonString() == JsonNode.Parse(source)!["parcels"]!.ToJsonString(), "Geometry edit preserves capture and timings");
    Reject(() => LayoutStore.Save(layout, "stale", source), "Concurrent editor cannot overwrite");
    var broken = layout.DeepClone().AsObject(); broken["nodes"]!.AsArray().RemoveAt(1);
    Reject(() => LayoutStore.Save(broken, revision, source), "Dangling endpoint rejected");
    broken = layout.DeepClone().AsObject(); broken["edges"]![0]!["points"]![0]![0] = 99;
    Reject(() => LayoutStore.Save(broken, revision, source), "Detached endpoint rejected");
    layout["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"J","label":"Junction","x":100,"y":0} """));
    layout["edges"] = JsonNode.Parse("""[{"id":"AJ","a":"A","b":"J","points":[[-50,0],[100,0]]},{"id":"JB","a":"J","b":"B","points":[[100,0],[100,100]]}]""");
    revision = LayoutStore.Save(layout, revision, source);
    projected = JsonNode.Parse(LayoutStore.Apply(source))!;
    var route = projected["edges"]!.AsArray().Last()!;
    Check(route["points"]!.ToJsonString() == "[[-50,0],[100,0],[100,100]]", "Deleted shortcut reroutes through junction with a real bend");
    Check(projected["parcels"]![0]!["events"]!.ToJsonString().Contains("original evidence"), "Rerouting preserves raw evidence");
    broken = layout.DeepClone().AsObject(); broken["edges"]!.AsArray().RemoveAt(1);
    Reject(() => LayoutStore.Save(broken, revision, source), "Disconnected recorded route cannot save");
    Check(Read()["revision"]!.GetValue<string>() == revision, "Failed save preserves last valid file");
    var other = JsonNode.Parse(source)!; other["parcels"]![0]!["stops"]![0]!["id"] = "MISSING";
    var incompatible = JsonNode.Parse(LayoutStore.Apply(other.ToJsonString()))!;
    Check(incompatible["layoutWarning"] is not null, "Incompatible capture reports fallback explicitly");
    Check(incompatible["nodes"]!.ToJsonString() == other["nodes"]!.ToJsonString(), "Incompatible capture remains playable with source geometry");
    var sentinel = JsonNode.Parse(source)!;
    sentinel["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"UNOBSERVED_POSITION","label":"Unknown","x":2400,"y":2600}"""));
    sentinel["parcels"]![0]!["stops"]!.AsArray().Insert(0, JsonNode.Parse("""{"id":"UNOBSERVED_POSITION"}"""));
    Check(JsonNode.Parse(LayoutStore.Apply(sentinel.ToJsonString()))!["layoutWarning"] is null, "Later captures retain their nonphysical unknown-position sentinel");
    layout["edges"]![0]!["evidence"] = "private capture record";
    LayoutStore.Save(layout, revision, source);
    Check(!File.ReadAllText(LayoutStore.FilePath).Contains("private capture record"), "Layout file excludes captured edge evidence");
    Console.WriteLine($"{passed} layout checks passed.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
