using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ParcelJourney.Core;

namespace ParcelJourney.App;

public static class CaptureReplay
{
    private static readonly object Sync = new();
    private static string? current;
    private static int loadedCount;
    public static int LoadedCount { get { lock (Sync) return loadedCount; } }
    public static void Clear() { lock (Sync) { current = null; loadedCount = 0; } }
    public static string Model { get { lock (Sync) { if(current is not null)return current;var empty=JsonNode.Parse(EmbeddedAssets.Read("ParcelJourney.3d.Model"))!.AsObject();empty["parcels"]=new JsonArray();empty["validation"]=new JsonObject{["mode"]="no-capture"};return empty.ToJsonString(); } } }
    public static string Viewer()
    {
        var html = EmbeddedAssets.Read("ParcelJourney.Viewer");
        html = Regex.Replace(html, @"(?m)^const M=.*;\r?$", _ => "const M=" + LayoutStore.Apply(Model) + ";");
        html = html.Replace(" completed real parcels", " captured parcels").Replace("No later movement in supplied logs.", "Capture coverage only; no later movement inferred.");
        html=html.Replace("evidence[0]?.LineNumber||item.line||'?'", "(evidence[0]?.ByteOffset!==undefined&&evidence[0]?.ByteOffset!==null?'byte '+evidence[0].ByteOffset:evidence[0]?.LineNumber||item.line||'?')").Replace("x.LineNumber||x.line||'?'", "(x.ByteOffset!==undefined&&x.ByteOffset!==null?'byte '+x.ByteOffset:x.LineNumber||x.line||'?')");
        html=html.Replace("detailPanel.append(dl);", "detailPanel.append(dl);if(p.captureMetadata){const metadata=document.createElement('details'),heading=document.createElement('summary'),pre=document.createElement('pre');heading.textContent='Stored parcel metadata';pre.textContent=JSON.stringify(p.captureMetadata,null,2);metadata.append(heading,pre);detailPanel.append(metadata);}");
        html=html.Replace("details.append(summary,pre);", "if(item.Outcome||item.Confidence||item.Gap){const facts=document.createElement('p');facts.textContent=[item.Outcome,item.Confidence,item.Gap].filter(Boolean).join(' · ');details.append(facts);}if(item.CaptureFields)pre.textContent+='\\n\\nStored PJ01 fields\\n'+JSON.stringify(item.CaptureFields,null,2);details.append(summary,pre);");
        html = html.Replace("return{action,role,summary:e.Summary||'Recorded exchange'", "if(e.CloseObservationNumber){action='CLOSE observation '+e.CloseObservationNumber;role='Database';}return{action,role,summary:e.Summary||'Recorded exchange'");
        return html;
    }
    public static string AddLoader(string html) => html + "<link rel=\"stylesheet\" href=\"/journey-browser.css\"><script src=\"/journey-browser.js\" defer></script>";
    private static string S(JsonNode? n) => n is null ? "" : n.ToString();
    private static long N(JsonNode? n) => n is null ? 0 : n.GetValue<long>();
    private static string Stamp(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms).ToString("yyyy.MM.dd HH:mm:ss.fff");
    private static string RouteAttemptSummary(JsonObject e)
    {
        string decision = S(e["scanner_decision_code"]), reported = S(e["scanner_reported_code"]), target = S(e["route_attempt_target_code"]);
        string text = decision == "WG" ? "Continue straight (WG)" : decision != "" ? "Output request " + decision : "PLC report " + reported;
        if (target != "") text += " → target " + target;
        if (reported != "") text += "; PLC reported " + reported;
        // PJ01 event fields can encode booleans as dictionary strings.
        if (bool.TryParse(S(e["route_attempt_destination_changed"]), out var changed) && changed) text += "; destination attempt changed " + S(e["route_attempt_previous_target_code"]) + " → " + target;
        if (S(e["scanner_decision_status"]) == "decision-plc-report-mismatch") text += "; commanded and reported branches differ";
        if (S(e["route_attempt_next_observed_location"]) != "") text += "; next scanner observed: " + S(e["route_attempt_next_observed_location"]);
        return text;
    }
    private static JsonObject Values(JsonObject e)
    {
        var result = new JsonObject();
        foreach (Match m in Regex.Matches(S(e["raw"]), @"\b(\w+)=([^,]*)(?:,|$)")) result[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('\'');
        foreach (var key in new[] { "Status", "TrackingId", "LastScanPos", "CarrierID", "ControlFlag", "OrderID", "CartonType", "PlcTarget", "WeightStart", "WeightBrutto", "GrossWeight", "VRNewHeight" }) if (e[key] is not null) result[key] = e[key]!.DeepClone();
        return result;
    }
    public static JsonObject Load(string selection, bool closedOnly = true, IReadOnlyCollection<string>? selectedFiles = null)
    {
        lock (Sync)
        {
            string full = Path.GetFullPath(selection), root = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
            while (!File.Exists(Path.Combine(root, "index.json"))) root = Directory.GetParent(root)?.FullName ?? throw new InvalidDataException("Select capture Data, its parcels folder, or a .pjourney file. The matching index and shared dictionary are required.");
            var index = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "index.json")))!.AsObject();
            string dictionaryPath = SafePath(root, S(index["dictionary"]));
            var dictionary = PjourneyReader.ReadDictionary(dictionaryPath, S(index["dictionary_id"]));
            var model = JsonNode.Parse(EmbeddedAssets.Read("ParcelJourney.3d.Model"))!.AsObject();
            var nodes = model["nodes"]!.AsArray(); var nodeMap = nodes.OfType<JsonObject>().ToDictionary(n => S(n["id"]));
            var edges = model["edges"]!.AsArray(); var edgeMap = edges.OfType<JsonObject>().ToDictionary(e => S(e["id"]));
            foreach (var edge in edgeMap.Values) { edge["evidence"] = new JsonArray(); edge["observationStatus"] = "Retained layout connection; not confirmed by this capture"; }
            foreach (var node in nodeMap.Values) node["observationStatus"] = "Retained layout asset; not confirmed by this capture";
            var parcels = new JsonArray(); var errors = new JsonArray(); var unknown = new HashSet<string>(); int eventTotal = 0;
            string scanFolder = Directory.Exists(Path.Combine(full,"parcels")) ? Path.Combine(full,"parcels") : full;
            var published = index["entries"]!.AsArray().OfType<JsonObject>().Select(e => SafePath(root, S(e["file"]))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = selectedFiles is not null ? selectedFiles.Select(Path.GetFullPath).OrderBy(p=>p,StringComparer.Ordinal).ToArray() : File.Exists(full) ? new[] {full} : published.Where(p => Path.GetFullPath(p).StartsWith(Path.GetFullPath(scanFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
            if (paths.Any(p => !published.Contains(p))) throw new InvalidDataException("Selected .pjourney is not part of the published index.");
            foreach (string candidate in paths)
            {
                string path = SafePath(root, Path.GetRelativePath(root,candidate));
                try
                {
                    var parcel = PjourneyReader.Read(path, dictionary);
                    var indexed = index["entries"]!.AsArray().OfType<JsonObject>().FirstOrDefault(e=>SafePath(root,S(e["file"])).Equals(path,StringComparison.OrdinalIgnoreCase));
                    if(indexed is not null && S(indexed["parcel"])!=S(parcel["parcel"]))throw new InvalidDataException("Index/parcel identity mismatch.");
                    if (indexed?["events"] is not null && N(indexed["events"]) != parcel["events"]!.AsArray().Count) throw new InvalidDataException("Index/parcel event count mismatch.");
                    var metadata=parcel.DeepClone().AsObject();metadata.Remove("events");
                    bool isClosed = parcel["closed"]?.GetValue<bool>() == true;
                    bool startObserved = parcel["start_observed"]?.GetValue<bool>() == true;
                    if (closedOnly && !isClosed) continue;
                    var recorded = parcel["events"]!.AsArray().OfType<JsonObject>().OrderBy(e => N(e["t"])).ThenBy(e => S(e["source"])).ThenBy(e => N(e["offset"])).ToList();
                    var routeAttempts = new JsonArray();
                    foreach (var routeEvent in recorded.Where(e => S(e["kind"]) == "scan" && S(e["scanner_decision_status"]) != ""))
                        routeAttempts.Add(new JsonObject { ["Timestamp"] = Stamp(N(routeEvent["t"])), ["Scanner"] = S(routeEvent["point"]), ["Decision"] = S(routeEvent["scanner_decision_code"]), ["PLCReport"] = S(routeEvent["scanner_reported_code"]), ["TargetCode"] = S(routeEvent["route_attempt_target_code"]), ["DestinationChanged"] = routeEvent["route_attempt_destination_changed"]?.DeepClone(), ["PreviousTargetCode"] = S(routeEvent["route_attempt_previous_target_code"]), ["Status"] = S(routeEvent["scanner_decision_status"]), ["Summary"] = RouteAttemptSummary(routeEvent) });
                    metadata["route_attempt_history"] = routeAttempts.DeepClone();
                    if (recorded.Count == 0) throw new InvalidDataException("Parcel has no events.");
                    var recordsById = recorded.Where(r => S(r["record_id"]) != "").ToLookup(r => S(r["record_id"]));
                    int closeOrdinal = 0; var closeObservations = new JsonArray();
                    long first = N(parcel["first"]); var events = new JsonArray(); var observations = new JsonArray(); var stops = new JsonArray(); var legs = new JsonArray();
                    string location = "UNOBSERVED_POSITION"; JsonObject? previous = null;
                    if (S(recorded[0]["kind"]) != "scan") { if(!nodeMap.ContainsKey(location)){var placeholder=new JsonObject { ["id"]=location,["label"]="Position not observed",["x"]=2400,["y"]=2600,["kind"]="junction",["physical"]=false,["coordinateBasis"]="Unknown position placeholder; not physical equipment"};nodes.Add(placeholder);nodeMap[location]=placeholder;}previous=new JsonObject { ["id"]=location,["arrival"]=0d,["departure"]=0d};stops.Add(previous); } var state = new JsonObject();
                    foreach (var e in recorded)
                    {
                        double t = (N(e["t"]) - first) / 1000d; if (t < 0) throw new InvalidDataException("Event precedes declared start.");
                        string kind = S(e["kind"]), observed = S(e["point"]); var values = kind == "state" ? Values(e) : new JsonObject();
                        if (kind == "state") { observed = S(e["LastScanPos"]); foreach (var v in values) state[v.Key] = v.Value?.DeepClone(); }
                        if (kind == "scan")
                        {
                            if (string.IsNullOrEmpty(observed) || observed.StartsWith("VR_")) throw new InvalidDataException("Scanner event lacks a physical scanner ID.");
                            if (!nodeMap.ContainsKey(observed))
                            {
                                unknown.Add(observed); var node = new JsonObject { ["id"] = observed, ["label"] = observed + " (unmapped scanner)", ["x"] = 2400 + 160 * unknown.Count, ["y"] = 2450, ["kind"] = "junction", ["coordinateBasis"] = "Unmapped equipment: placeholder position", ["equipment"] = S(e["position"]) }; nodes.Add(node); nodeMap[observed] = node;
                            }
                            nodeMap[observed]["observationStatus"] = "Observed in live capture";
                            if (previous is null || S(previous["id"]) != observed)
                            {
                                if (previous is not null)
                                {
                                    string a = S(previous["id"]), key = a + ">" + observed;
                                    bool physicalEdge = edgeMap.TryGetValue(key, out var matchingEdge) && S(matchingEdge["a"]) == a && S(matchingEdge["b"]) == observed;
                                    if (physicalEdge) matchingEdge!["observationStatus"] = "Observed scanner succession matches this directed topology edge; exact geometry remains schematic";
                                    double departure = previous["departure"]!.GetValue<double>();
                                    if (a != "UNOBSERVED_POSITION" && t > departure) legs.Add(new JsonObject { ["a"] = a, ["b"] = observed, ["start"] = departure, ["end"] = t, ["edges"] = physicalEdge ? new JsonArray(key) : new JsonArray(), ["routeStatus"] = physicalEdge ? "topology-edge" : "unknown", ["positionBasis"] = physicalEdge ? "Interpolated along the matching directed topology edge; geometry remains schematic" : "Observed scanner succession; physical corridor is not mapped" });
                                }
                                previous = new JsonObject { ["id"] = observed, ["arrival"] = t, ["departure"] = t }; stops.Add(previous);
                            }
                            location = observed;
                        }
                        // Software traffic and LastScanPos snapshots do not establish
                        // physical dwell. In particular, the next scanner's transport
                        // receipt must not postpone departure from the previous scanner.
                        if (kind == "scan" && previous is not null) previous["departure"] = t;
                        string summary = kind switch { "scan" => "Scanner read | " + observed, "ack" => "PLC reported " + S(e["reported"]) + " | " + observed, "command" => "PLC requested " + S(e["requested"]), "state" => "Database " + S(e["Status"]) + " | " + observed, "mismatch" => "Destination mismatch | " + observed, _ => "Recorded exchange | " + S(e["module"]) };
                        if (S(e["summary"]) != "") summary = S(e["summary"]);
                        int? closeNumber = null;
                        if (kind == "state" && S(e["Status"]) == "CLOSED")
                        {
                            closeNumber = ++closeOrdinal;
                            summary += " | CLOSE observation " + closeOrdinal;
                            closeObservations.Add(new JsonObject { ["ordinal"] = closeOrdinal, ["Timestamp"] = Stamp(N(e["t"])), ["LocationId"] = observed, ["Source"] = S(e["source"]), ["Offset"] = N(e["offset"]), ["Basis"] = "Recorded database closure; refeed not established by closure alone" });
                        }
                        var recordEvidence = new JsonArray();
                        foreach(var lineEvent in S(e["record_id"]) == "" || N(e["record_line"]) != 0 ? new[] { e }.AsEnumerable() : recordsById[S(e["record_id"])]) recordEvidence.Add(new JsonObject { ["FileName"] = S(lineEvent["source"]), ["LineNumber"] = S(lineEvent["offset_unit"]) == "line" ? N(lineEvent["offset"]) : null, ["ByteOffset"] = S(lineEvent["offset_unit"]) == "line" ? null : N(lineEvent["offset"]), ["RawLine"] = S(lineEvent["raw"]), ["Timestamp"] = Stamp(N(lineEvent["t"])) });
                        var item = new JsonObject { ["CloseObservationNumber"] = closeNumber, ["Outcome"] = S(e["outcome"]), ["Confidence"] = S(e["identity_confidence"]), ["RecordId"] = S(e["record_id"]), ["OperationId"] = S(e["operation_id"]), ["Gap"] = S(e["record_scope"]) == "orphan" ? "Missing source record header" : S(e["operation_scope"]) != "" ? S(e["operation_scope"]) : S(e["semantic_scope"]).StartsWith("raw-only") ? S(e["semantic_scope"]) : "", ["t"] = t, ["LocationId"] = location, ["ObservedLocationId"] = observed, ["Summary"] = summary, ["EventType"] = S(e["event_type"]) != "" ? S(e["event_type"]) : kind == "scan" ? "OnScannerData" : kind == "ack" ? "OnPlcAcknowledge" : kind, ["Timestamp"] = Stamp(N(e["t"])), ["Evidence"] = recordEvidence, ["Raw"] = S(e["raw"]), ["Source"] = S(e["source"]), ["values"] = kind == "state" ? values.DeepClone() : null, ["routingTab"] = observed.StartsWith("VR_") ? observed : null, ["CaptureFields"] = e.DeepClone(), ["requested"] = e["requested"]?.DeepClone(), ["reported"] = e["reported"]?.DeepClone() };
                        events.Add(item);
                        observations.Add(new JsonObject { ["t"] = t, ["Timestamp"] = Stamp(N(e["t"])), ["raw"] = S(e["raw"]), ["values"] = kind == "state" ? values.DeepClone() : null, ["LocationId"] = observed, ["Layer"] = kind == "state" ? "Db" : kind == "ack" || kind == "command" || S(e["source"]).Contains("PLC") ? "PLC" : S(e["source"]).Contains("LVS") ? "Transport" : "Logic", ["system"] = S(e["module"]), ["sourceFile"] = S(e["source"]), ["line"] = (S(e["offset_unit"]) == "line" ? "line " : "byte ") + N(e["offset"]), ["captureFields"] = e.DeepClone(), ["index"] = observations.Count });
                    }
                    if (stops.Count == 0) throw new InvalidDataException("No displayable event position.");
                    var lastScan = recorded.LastOrDefault(e => S(e["kind"]) == "scan");
                    string terminalNode = S(lastScan?["terminal_exit_node"]);
                    if (terminalNode == "") terminalNode = S(lastScan?["terminal_outlet_node"]);
                    if (terminalNode != "" && nodeMap.ContainsKey(terminalNode))
                    {
                        string outlet = terminalNode;
                        long terminalReportTime = N(lastScan?["terminal_exit_report_t"]);
                        if (terminalReportTime == 0) terminalReportTime = N(lastScan?["terminal_report_t"]);
                        double reportTime = (terminalReportTime - first) / 1000d;
                        double departure = previous!["departure"]!.GetValue<double>();
                        if (reportTime > departure)
                        {
                            string edgeId = S(lastScan?["point"]) + ">" + outlet;
                            bool mappedExitEdge = edgeMap.TryGetValue(edgeId, out var exitEdge) && S(exitEdge["a"]) == S(lastScan?["point"]) && S(exitEdge["b"]) == outlet;
                            if (mappedExitEdge) exitEdge!["observationStatus"] = "PLC output and MFC closure observed at the decision scanner; downstream movement is reported, geometry remains schematic";
                            var terminalEdgeText = S(lastScan?["terminal_route_edge_ids_json"]);
                            var terminalEdges = string.IsNullOrWhiteSpace(terminalEdgeText) ? new JsonArray() : JsonNode.Parse(terminalEdgeText) as JsonArray ?? new JsonArray();
                            if (terminalEdges.Count == 0 && mappedExitEdge) terminalEdges.Add(edgeId);
                            string captureRouteStatus = S(lastScan?["terminal_exit_status"]) != "" ? "plc-reported-output-corridor" : S(lastScan?["terminal_route_status"]);
                            legs.Add(new JsonObject { ["a"] = S(lastScan["point"]), ["b"] = outlet, ["start"] = departure, ["end"] = reportTime,
                                ["edges"] = mappedExitEdge ? new JsonArray(edgeId) : new JsonArray(), ["captureRouteStatus"] = captureRouteStatus,
                                ["captureRouteEdgeIds"] = terminalEdges,
                                ["captureStepId"] = S(lastScan?["step_id"]), ["reportedOutletEvidenceId"] = S(lastScan?["terminal_exit_report_event_id"]) != "" ? S(lastScan?["terminal_exit_report_event_id"]) : S(lastScan?["terminal_report_event_id"]),
                                ["positionBasis"] = S(lastScan?["terminal_exit_position_basis"]) != "" ? S(lastScan?["terminal_exit_position_basis"]) : S(lastScan?["terminal_position_basis"]), ["routeStatus"] = mappedExitEdge ? "reported-output-candidate" : "unknown" });
                            previous = new JsonObject { ["id"] = outlet, ["arrival"] = reportTime, ["departure"] = reportTime,
                                ["positionBasis"] = S(lastScan?["terminal_exit_position_basis"]) != "" ? S(lastScan?["terminal_exit_position_basis"]) : S(lastScan?["terminal_position_basis"]), ["observationKind"] = "plc-reported-output" };
                            stops.Add(previous);
                        }
                    }
                    foreach (var leg in legs.OfType<JsonObject>())
                    {
                        var departure = first + (long)Math.Round(leg["start"]!.GetValue<double>() * 1000);
                        var step = recorded.LastOrDefault(e => S(e["kind"]) == "scan" && N(e["t"]) == departure && S(e["point"]) == S(leg["a"]) && S(e["next_observed_location"]) == S(leg["b"]));
                        if (step?["route_status"] is null) continue;
                        leg["captureStepId"] = S(step["step_id"]);
                        leg["captureRouteStatus"] = S(step["route_status"]);
                        var capturedEdgeText = S(step["route_edge_ids_json"]);
                        leg["captureRouteEdgeIds"] = string.IsNullOrWhiteSpace(capturedEdgeText) ? new JsonArray() : JsonNode.Parse(capturedEdgeText) ?? new JsonArray();
                        leg["routeReason"] = S(step["route_unresolved_reason"]);
                        leg["positionBasis"] = S(step["route_basis"]) + " " + S(step["route_geometry_basis"]);
                    }
                    double duration = events.Last()!["t"]!.GetValue<double>(); previous!["departure"] = duration;
                    string line = stops.OfType<JsonObject>().Select(s => S(s["id"])).FirstOrDefault(s => s.StartsWith("SC_BWL"))?.Replace("SC_BWL", "") ?? "unknown";
                    var replay = new JsonObject { ["parcelId"] = S(parcel["parcel"]), ["visitKey"] = Path.GetFileNameWithoutExtension(path), ["closeCount"] = closeOrdinal, ["closeObservations"] = closeObservations, ["id"] = S(parcel["parcel"]), ["name"] = S(state["CarrierID"]) + " | line " + line + " | " + (startObserved ? "entry captured" : "entry unobserved") + " | " + Stamp(N(parcel["first"])), ["events"] = events, ["observations"] = observations, ["stops"] = stops, ["legs"] = legs, ["duration"] = duration, ["completeness"] = isClosed ? "CLOSED observed; " + closeOrdinal + " closure records; " + (startObserved ? "" : "entry not captured; ") + "capture coverage only" : "Partial capture; no CLOSED observed", ["outcome"] = isClosed ? "completed" : "partial", ["captureMetadata"] = metadata, ["fixture"] = new JsonObject(), ["source"] = "pjourney-capture", ["aliases"] = parcel["aliases"]?.DeepClone() ?? new JsonArray(), ["proof"] = new JsonObject { ["closedObserved"] = isClosed, ["startObserved"] = startObserved, ["completePhysicalCoverage"] = false, ["sourceFile"] = Path.GetFileName(path) } };
                    eventTotal += events.Count; if(eventTotal>500000)throw new JourneyCapacityException("Journey load exceeds 500,000 events. Choose a smaller subfolder or load a selected ParcelID."); parcels.Add(replay);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or InvalidOperationException or OverflowException) { errors.Add(new JsonObject { ["file"] = Path.GetFileName(path), ["error"] = ex.Message }); }
            }
            if (parcels.Count == 0) throw new InvalidDataException("No readable " + (closedOnly ? "CLOSED " : "") + "parcels found. " + errors.ToJsonString());
            var validation = new JsonObject { ["mode"] = "pjourney-capture", ["runs"] = parcels.Count, ["closedOnly"] = closedOnly, ["events"] = eventTotal, ["filesChecked"] = paths.Length, ["completeLifecycles"] = false, ["scope"] = "Parser-associated evidence only. CLOSED is a database observation; physical geometry and movement between scans are schematic.", ["unmappedScanners"] = new JsonArray(unknown.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["errors"] = errors };
            model["parcels"] = parcels; model["validation"] = validation; model.Remove("sourceManifest");
            CorridorRoutes.Resolve(model);
            current = model.ToJsonString(); loadedCount = parcels.Count;
            return validation;
        }
    }
    public static JsonObject LoadFiles(IReadOnlyCollection<JourneyFile> files)
    {
        if (files.Count == 0 || files.Count > 10000) throw new InvalidDataException("Select between 1 and 10,000 published journeys.");
        lock (Sync)
        {
            var previous = current; var previousCount = loadedCount;
            var combined = JsonNode.Parse(EmbeddedAssets.Read("ParcelJourney.3d.Model"))!.AsObject();
            var parcels = new JsonArray(); var errors = new JsonArray(); int events = 0, sources = 0;
            var nodes = combined["nodes"]!.AsArray(); var nodeIds = nodes.Select(n => S(n!["id"])).ToHashSet();
            try
            {
                foreach (var group in files.DistinctBy(f=>f.File,StringComparer.OrdinalIgnoreCase).GroupBy(f => f.DatasetRoot))
                {
                    if (group.Key is null) throw new InvalidDataException("Matching index and dictionary are required to load .pjourney files.");
                    var report = Load(group.Key, false, group.Select(f=>f.File).ToArray()); sources++;
                    var model = JsonNode.Parse(current!)!;
                    foreach (var n in model["nodes"]!.AsArray()) if (nodeIds.Add(S(n!["id"]))) nodes.Add(n!.DeepClone());
                    foreach (var parcel in model["parcels"]!.AsArray())
                    {
                        var clone = parcel!.DeepClone(); clone["captureFolder"] = group.Key; parcels.Add(clone);
                    }
                    events += report["events"]!.GetValue<int>();
                    if(events>500000)throw new JourneyCapacityException("Journey load exceeds 500,000 events. Choose a smaller subfolder or load a selected ParcelID.");
                    foreach (var error in report["errors"]!.AsArray()) errors.Add(error!.DeepClone());
                }
                var validation = new JsonObject { ["mode"]="pjourney-capture", ["runs"]=parcels.Count, ["events"]=events, ["sources"]=sources, ["filesChecked"]=files.Count, ["errors"]=errors, ["completeLifecycles"]=false };
                combined["parcels"] = parcels; combined["validation"] = validation;
                CorridorRoutes.Resolve(combined); current = combined.ToJsonString(); loadedCount = parcels.Count;
                return validation;
            }
            catch { current = previous; loadedCount = previousCount; throw; }
        }
    }

    private static string SafePath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture index path outside Data.");
        return full;
    }
}

public sealed class JourneyCapacityException(string message) : Exception(message);
