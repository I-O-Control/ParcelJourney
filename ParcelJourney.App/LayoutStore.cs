using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ParcelJourney.App;

// Physical geometry is independent of capture evidence. One local plant layout,
// shared by both renderers and persisted across the host's ephemeral HTTP ports.
public static class LayoutStore
{
    private static readonly object Sync = new();
    public static string FilePath => Environment.GetEnvironmentVariable("PARCELJOURNEY_LAYOUT_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParcelJourney", "layout.json");
    private static string? Read() => File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
    private static string Revision(string? value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
    private static JsonObject Geometry(JsonObject value)
    {
        JsonArray Select(string collection, string[] fields) => new(value[collection]!.AsArray().Select(item => {
            var clean = new JsonObject(); foreach (var field in fields) if (item![field] is not null) clean[field] = item[field]!.DeepClone(); return (JsonNode)clean;
        }).ToArray());
        return new JsonObject { ["version"] = 1,
            ["nodes"] = Select("nodes", ["id", "label", "x", "y", "kind", "equipment", "section"]),
            ["edges"] = Select("edges", ["id", "a", "b", "points"]) };
    }
    public static object Get(string source)
    {
        lock (Sync)
        {
            var saved = Read(); var model = JsonNode.Parse(source)!.AsObject();
            return new { revision = Revision(saved), layout = saved is null ? Geometry(model) : JsonNode.Parse(saved), model, path = FilePath };
        }
    }
    private static string Id(JsonNode? n) => n?.GetValue<string>() ?? throw new InvalidDataException("Missing identifier.");
    private static double Number(JsonNode? n)
    {
        if (n is null || !double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || Math.Abs(v) > 1000000) throw new InvalidDataException("Coordinates must be finite and within ±1,000,000.");
        return v;
    }
    public static void Validate(JsonObject layout)
    {
        if (layout["version"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported layout version.");
        var nodes = layout["nodes"]?.AsArray() ?? throw new InvalidDataException("Missing nodes.");
        var edges = layout["edges"]?.AsArray() ?? throw new InvalidDataException("Missing edges.");
        if (nodes.Count is < 1 or > 10000 || edges.Count > 30000) throw new InvalidDataException("Invalid layout size.");
        var map = new Dictionary<string, JsonNode>();
        foreach (var n in nodes)
        {
            var id = Id(n?["id"]);
            if (id.Length is < 1 or > 120 || !map.TryAdd(id, n!)) throw new InvalidDataException("Station IDs must be unique and nonempty.");
            Number(n!["x"]); Number(n["y"]);
            if (string.IsNullOrWhiteSpace(n["label"]?.GetValue<string>())) throw new InvalidDataException("Every station needs a label.");
        }
        var ids = new HashSet<string>();
        foreach (var e in edges)
        {
            if (!ids.Add(Id(e?["id"]))) throw new InvalidDataException("Route IDs must be unique.");
            if (!map.TryGetValue(Id(e!["a"]), out var a) || !map.TryGetValue(Id(e["b"]), out var b)) throw new InvalidDataException("A route references a missing station.");
            var points = e["points"]!.AsArray();
            if (points.Count is < 2 or > 2000) throw new InvalidDataException("Routes need 2–2000 points.");
            foreach (var p in points) { if (p is not JsonArray xy || xy.Count != 2) throw new InvalidDataException("Invalid route point."); Number(p[0]); Number(p[1]); }
            if (Number(points[0]![0]) != Number(a["x"]) || Number(points[0]![1]) != Number(a["y"]) || Number(points[^1]![0]) != Number(b["x"]) || Number(points[^1]![1]) != Number(b["y"])) throw new InvalidDataException("Route endpoints must remain attached to their stations.");
        }
    }
    private static JsonObject Project(string source, JsonObject layout)
    {
        Validate(layout);
        var model = JsonNode.Parse(source)!.AsObject();
        var nodes = layout["nodes"]!.DeepClone().AsArray(); var edges = layout["edges"]!.AsArray();
        // This is a replay sentinel, not a physical station owned by the layout.
        var unobserved = model["nodes"]!.AsArray().FirstOrDefault(n => Id(n!["id"]) == "UNOBSERVED_POSITION");
        if (unobserved is not null && !nodes.Any(n => Id(n!["id"]) == "UNOBSERVED_POSITION")) nodes.Add(unobserved.DeepClone());
        var ids = nodes.Select(n => Id(n!["id"])).ToHashSet();
        var edgeMap = edges.ToDictionary(e => Id(e!["id"]), e => e!);
        var outgoing = edges.GroupBy(e => Id(e!["a"])).ToDictionary(g => g.Key, g => g.ToArray());
        var rendered = edges.DeepClone().AsArray();
        var routes = new Dictionary<string, string>();
        foreach (var parcel in model["parcels"]!.AsArray())
        {
            foreach (var stop in parcel!["stops"]!.AsArray()) if (!ids.Contains(Id(stop!["id"]))) throw new InvalidDataException("Layout is missing recorded station " + Id(stop["id"]) + ". Restore it before applying this capture.");
            foreach (var leg in parcel["legs"]!.AsArray())
            {
                var a = Id(leg!["a"]); var b = Id(leg["b"]); var key = a + "\n" + b;
                var original = Id(leg["edges"]![0]);
                if (edgeMap.TryGetValue(original, out var direct) && Id(direct["a"]) == a && Id(direct["b"]) == b) continue;
                if (!routes.TryGetValue(key, out var routeId))
                {
                    // Directed shortest physical path, only along user-supplied connections.
                    var queue = new PriorityQueue<string, double>(); queue.Enqueue(a, 0);
                    var distance = new Dictionary<string, double> { [a] = 0 }; var previous = new Dictionary<string, JsonNode>();
                    while (queue.TryDequeue(out var node, out var cost))
                    {
                        if (node == b) break;
                        if (cost > distance[node] || !outgoing.TryGetValue(node, out var next)) continue;
                        foreach (var e in next)
                        {
                            var ps = e!["points"]!.AsArray(); double length = 0;
                            for (int i = 1; i < ps.Count; i++) length += Math.Sqrt(Math.Pow(Number(ps[i]![0]) - Number(ps[i-1]![0]), 2) + Math.Pow(Number(ps[i]![1]) - Number(ps[i-1]![1]), 2));
                            var target = Id(e["b"]); var total = cost + Math.Max(length, .001);
                            if (!distance.TryGetValue(target, out var old) || total < old) { distance[target] = total; previous[target] = e; queue.Enqueue(target, total); }
                        }
                    }
                    if (!distance.ContainsKey(b)) throw new InvalidDataException($"No directed physical route from {a} to {b}. Add a connection before saving.");
                    var chain = new List<JsonNode>(); for (var at = b; at != a;) { var e = previous[at]; chain.Add(e); at = Id(e["a"]); } chain.Reverse();
                    var points = new JsonArray(); foreach (var e in chain) foreach (var p in e["points"]!.AsArray().Skip(points.Count == 0 ? 0 : 1)) points.Add(p!.DeepClone());
                    routeId = "layout-replay-" + Guid.NewGuid().ToString("N"); routes[key] = routeId;
                    rendered.Add(new JsonObject { ["id"] = routeId, ["a"] = a, ["b"] = b, ["points"] = points, ["basis"] = "User layout: shortest directed physical path between recorded stations" });
                }
                leg["edges"] = new JsonArray(routeId);
            }
        }
        var originalNodes = model["nodes"]!.AsArray().ToDictionary(n => Id(n!["id"]));
        var merged = new JsonArray();
        foreach (var n in nodes) {
            var value = originalNodes.TryGetValue(Id(n!["id"]), out var originalNode) ? originalNode!.DeepClone().AsObject() : new JsonObject();
            foreach (var property in n!.AsObject()) value[property.Key] = property.Value?.DeepClone();
            value["coordinateBasis"] = "User physical layout"; merged.Add(value);
        }
        model["nodes"] = merged; model["edges"] = rendered;
        return model;
    }
    public static string Apply(string source)
    {
        lock (Sync)
        {
            try { var saved = Read(); return saved is null ? source : Project(source, JsonNode.Parse(saved)!.AsObject()).ToJsonString(); }
            catch (Exception ex) { var model = JsonNode.Parse(source)!.AsObject(); model["layoutWarning"] = "Saved layout was not applied: " + ex.Message; return model.ToJsonString(); }
        }
    }
    public static string Save(JsonObject layout, string revision, string source)
    {
        lock (Sync)
        {
            if (revision != Revision(Read())) throw new InvalidOperationException("Layout changed in another window. Reload the editor before saving.");
            Project(source, layout);
            // Persist geometry only; never store or overwrite parcel evidence here.
            var clean = Geometry(layout);
            var json = clean.ToJsonString(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporary = FilePath + ".tmp"; File.WriteAllText(temporary, json); File.Move(temporary, FilePath, true);
            return Revision(json);
        }
    }
}
