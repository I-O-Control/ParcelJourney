using System.Text.Json.Nodes;

namespace ParcelJourney.App;

// Resolve only an unambiguous sequence of explicitly modeled directed belts.
// A scanner observation cannot justify an arbitrary detour through other scanners.
public static class CorridorRoutes
{
    private static bool SameCoordinate(JsonNode? a, JsonNode? b) =>
        double.TryParse(a?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
        && double.TryParse(b?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
        && double.IsFinite(x) && double.IsFinite(y) && x == y;
    public static List<JsonNode>? Find(string from, string to, JsonArray edges, string? via = null)
    {
        var outgoing = edges.Where(e => e?["topology"]?.ToString() == "floor-layout" || e?["userDefined"]?.GetValue<bool>() == true)
            .GroupBy(e => e!["a"]!.ToString()).ToDictionary(g => g.Key, g => g.ToArray());
        var visited = new HashSet<string> { from };
        var path = new List<JsonNode>(); List<JsonNode>? unique = null;
        int matches = 0, explored = 0;
        void Walk(string at)
        {
            if (matches > 1 || ++explored > 10000) return;
            if (at == to) { if (via is null || path.Any(e => e["b"]!.ToString() == via)) { matches++; unique = path.ToList(); } return; }
            if (at != from && at.StartsWith("SC_", StringComparison.Ordinal)) return;
            if (!outgoing.TryGetValue(at, out var next)) return;
            foreach (var edge in next)
            {
                string target = edge!["b"]!.ToString();
                if (!visited.Add(target)) continue;
                path.Add(edge); Walk(target); path.RemoveAt(path.Count - 1); visited.Remove(target);
                if (matches > 1 || explored > 10000) return;
            }
        }
        Walk(from);
        return matches == 1 && explored <= 10000 ? unique : null;
    }

    public static void Resolve(JsonObject model)
    {
        var edges = model["edges"]!.AsArray();
        var nodes = model["nodes"]!.AsArray().ToDictionary(n => n!["id"]!.ToString());
        foreach (var parcel in model["parcels"]!.AsArray())
            foreach (var leg in parcel!["legs"]!.AsArray())
            {
                var from = leg!["a"]!.ToString();
                if (leg["captureRouteStatus"] is not null)
                {
                    var status = leg["captureRouteStatus"]!.ToString();
                    if (status == "co-located-read-head-change")
                    {
                        string to = leg["b"]!.ToString();
                        bool paired = System.Text.RegularExpressions.Regex.IsMatch(from, @"^SC_ETIO[ST]L[1-5]$")
                            && (from.Replace("SC_ETIOSL", "SC_ETIOTL") == to || from.Replace("SC_ETIOTL", "SC_ETIOSL") == to) && from != to;
                        bool samePlace = paired && nodes.TryGetValue(from, out var a) && nodes.TryGetValue(to, out var b)
                            && SameCoordinate(a!["x"], b!["x"]) && SameCoordinate(a["y"], b["y"]);
                        leg["edges"] = new JsonArray();
                        leg["routeStatus"] = samePlace ? status : "unknown";
                        leg["routeReason"] = samePlace ? "Side and top readers observe the same physical station; no conveyor movement is implied."
                            : "Co-located reader evidence does not match the current station coordinates.";
                        continue;
                    }
                    var ids = leg["captureRouteEdgeIds"]?.AsArray() ?? new JsonArray();
                    var map = edges.ToDictionary(e => e!["id"]!.ToString());
                    string at = from;
                    bool valid = (status is "modeled-corridor" or "reported-branch-corridor" or "reported-outlet-corridor" or "plc-reported-output-corridor") && ids.Count > 0;
                    foreach (var id in ids)
                    {
                        if (!valid || !map.TryGetValue(id!.ToString(), out var edge) || edge!["a"]!.ToString() != at) { valid = false; break; }
                        at = edge["b"]!.ToString();
                        if (at.StartsWith("SC_", StringComparison.Ordinal) && at != leg["b"]!.ToString()) { valid = false; break; }
                    }
                    valid &= at == leg["b"]!.ToString();
                    leg["edges"] = valid ? ids.DeepClone() : new JsonArray();
                    leg["routeStatus"] = valid ? status : "unknown";
                    if (!valid && status != "unknown") leg["routeReason"] = "Recorded corridor is not connected in the current layout; restore the catalog edges or supply engineering evidence for a replacement.";
                    continue;
                }
                string? via = null;
                if (from is "SC_VL1" or "SC_AP")
                {
                    var targets = parcel["events"]!.AsArray().Where(e => e?["CaptureFields"]?["kind"]?.ToString() == "ack"
                        && e["CaptureFields"]!["point"]?.ToString() == from
                        && e["t"]!.GetValue<double>() >= leg["start"]!.GetValue<double>()
                        && e["t"]!.GetValue<double>() < leg["end"]!.GetValue<double>())
                        .Select(e => e!["CaptureFields"]!["reported"]?.ToString()).Distinct().ToArray();
                    if (targets.Length == 1 && int.TryParse(targets[0], out int target)
                        && target >= 1 && target <= (from == "SC_VL1" ? 4 : 10))
                        via = (from == "SC_VL1" ? "SEAL" : "AP") + target;
                }
                if (via is null && leg["edges"]!.AsArray().Count != 0) continue;
                var path = Find(from, leg["b"]!.ToString(), edges, via);
                if (via is not null && path is null) { leg["edges"] = new JsonArray(); leg["routeStatus"] = "unknown"; }
                if (path is null) continue;
                leg["edges"] = new JsonArray(path.Select(e => (JsonNode?)JsonValue.Create(e["id"]!.ToString())).ToArray());
                leg["routeStatus"] = "topology-corridor";
                leg["positionBasis"] = via is null ? "Interpolated along the unique modeled directed corridor; intermediate equipment is unobserved and geometry is schematic"
                    : "Interpolated along modeled belts via " + via + "; branch selected by recorded PLC acknowledgement, geometry is schematic";
            }
        MarkPossibleExternalHandling(model);
    }

    private static void MarkPossibleExternalHandling(JsonObject model)
    {
        var outgoing = model["edges"]!.AsArray().GroupBy(e => e!["a"]!.ToString())
            .ToDictionary(g => g.Key, g => g.Select(e => e!["b"]!.ToString()).ToArray());
        var known = model["nodes"]!.AsArray().Where(n => n?["equipment"] is not null && !(n!["coordinateBasis"]?.ToString() ?? "").Contains("Unmapped"))
            .Select(n => n!["id"]!.ToString()).ToHashSet();
        bool Reachable(string from, string to)
        {
            var seen = new HashSet<string> { from }; var queue = new Queue<string>(); queue.Enqueue(from);
            while (queue.TryDequeue(out var at))
                if (outgoing.TryGetValue(at, out var next))
                    foreach (var destination in next) { if (destination == to) return true; if (seen.Add(destination)) queue.Enqueue(destination); }
            return false;
        }
        foreach (var parcel in model["parcels"]!.AsArray())
            foreach (var leg in parcel!["legs"]!.AsArray())
            {
                string from = leg!["a"]!.ToString(), to = leg["b"]!.ToString();
                bool possible = leg["routeStatus"]?.ToString() == "unknown" && known.Contains(from) && known.Contains(to) && !Reachable(from, to);
                leg["possibleExternalHandling"] = possible;
                if (possible) leg["handlingBasis"] = "No directed conveyor path connects the recorded scanners. Manual handling, missing topology or an association error remain possible; manual handling is not confirmed.";
            }
    }
}
