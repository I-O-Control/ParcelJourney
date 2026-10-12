using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ParcelJourney.Core;

namespace ParcelJourney.App;

public static class CaptureReplay
{
    private static readonly object Sync = new();
    private static string? current;
    public static string Model { get { if(current is not null)return current;var empty=JsonNode.Parse(EmbeddedAssets.Read("ParcelJourney.3d.Model"))!.AsObject();empty["parcels"]=new JsonArray();empty["validation"]=new JsonObject{["mode"]="no-capture"};return empty.ToJsonString(); } }
    public static string Viewer()
    {
        var html = EmbeddedAssets.Read("ParcelJourney.Viewer");
        if (current is null) return "<html><head><title>ParcelJourney</title><link rel='stylesheet' href='/theme.css'></head><body style='background:#101b30;color:#e2e8f0;font:16px sans-serif;padding:32px'><h1>ParcelJourney</h1><p>Choose a folder containing your parcel capture.</p><select id='pick' disabled><option>No capture loaded</option></select><p><a href='/3d'>3D viewer</a></p></body></html>";
        html = Regex.Replace(html, @"(?m)^const M=.*;\r?$", _ => "const M=" + LayoutStore.Apply(current) + ";");
        html = html.Replace(" completed real parcels", " captured parcels").Replace("No later movement in supplied logs.", "Capture coverage only; no later movement inferred.");
        html=html.Replace("evidence[0]?.LineNumber||item.line||'?'", "(evidence[0]?.ByteOffset!==undefined&&evidence[0]?.ByteOffset!==null?'byte '+evidence[0].ByteOffset:evidence[0]?.LineNumber||item.line||'?')").Replace("x.LineNumber||x.line||'?'", "(x.ByteOffset!==undefined&&x.ByteOffset!==null?'byte '+x.ByteOffset:x.LineNumber||x.line||'?')");
        html=html.Replace("detailPanel.append(dl);", "detailPanel.append(dl);if(p.captureMetadata){const metadata=document.createElement('details'),heading=document.createElement('summary'),pre=document.createElement('pre');heading.textContent='Stored parcel metadata';pre.textContent=JSON.stringify(p.captureMetadata,null,2);metadata.append(heading,pre);detailPanel.append(metadata);}");
        html=html.Replace("details.append(summary,pre);", "if(item.Outcome||item.Confidence||item.Gap){const facts=document.createElement('p');facts.textContent=[item.Outcome,item.Confidence,item.Gap].filter(Boolean).join(' · ');details.append(facts);}if(item.CaptureFields)pre.textContent+='\\n\\nStored PJ01 fields\\n'+JSON.stringify(item.CaptureFields,null,2);details.append(summary,pre);");
        html = html.Replace("return{action,role,summary:e.Summary||'Recorded exchange'", "if(e.CloseObservationNumber){action='CLOSE observation '+e.CloseObservationNumber;role='Database';}return{action,role,summary:e.Summary||'Recorded exchange'");
        return html;
    }
    public static string AddLoader(string html) => html + """
<script>
(() => {
  const editorLink = document.createElement('a');
  editorLink.href = '/layout-editor' + (location.pathname.startsWith('/3d') ? '?return=3d' : '');
  editorLink.textContent = 'Edit layout';
  editorLink.style.cssText = 'display:inline-block;padding:8px 12px;margin:6px;color:#60cdff;border:1px solid #506078;border-radius:6px;text-decoration:none';
  (document.querySelector('header') || document.body).append(editorLink);
  fetch('/3d/model.json').then(r=>r.json()).then(model=>{if(model.layoutWarning){const warning=document.createElement('p');warning.textContent=model.layoutWarning;warning.style.cssText='color:#ffc885;padding:8px';editorLink.after(warning);}}).catch(()=>{});
  // Both viewers use the same loader, placed beside their parcel selector.
  const select = document.getElementById('scenario') || document.getElementById('pick');
  if (!select) return;

  const box = document.createElement('div');
  box.style.marginBottom = '12px';
  const browse = document.createElement('button');
  browse.type = 'button';
  browse.textContent = 'Browse parcel folder';
  browse.style.width = '100%';

  // Use the browser picker: a native server-side dialog can leave the page waiting.
  const input = document.createElement('input');
  input.type = 'file';
  input.setAttribute('webkitdirectory', '');
  input.multiple = true;
  input.hidden = true;

  const status = document.createElement('p');
  status.style.fontSize = '12px';
  status.style.overflowWrap = 'anywhere';
  status.setAttribute('role', 'status');
  box.append(browse, input, status);
  select.before(box);
  const label = document.querySelector('label[for="scenario"]');
  if (label) label.textContent = 'Parcel capture';

  browse.onclick = () => input.click();
  input.onchange = async () => {
    if (!input.files.length) return;
    browse.disabled = true;
    try {
      const body = new FormData();
      let count = 0;
      // PJ01 needs its index and shared dictionary. Exclude parser working data.
      for (const file of input.files) {
        const name = file.webkitRelativePath || file.name;
        if (name.endsWith('.pjourney') || name.endsWith('/index.json') ||
            /\/dictionaries\/[^/]+\.json$/.test(name)) {
          body.append('files', file, name);
          count++;
        }
      }
      if (!count) throw Error('No capture files. Select Data containing index.json and dictionaries.');
      status.textContent = 'Loading ' + count + ' capture files...';
      const response = await fetch('/api/capture/upload', { method: 'POST', body });
      const report = await response.json();
      if (!response.ok) throw Error(report.error);

      // Reload only after the server has decoded and installed the selected model.
      sessionStorage.setItem('parceljourney-capture-folder', input.files[0].webkitRelativePath.split('/')[0]);
      location.reload();
    } catch (error) {
      status.textContent = error.message;
    } finally {
      // Allow retrying, including selecting the same folder after an error.
      browse.disabled = false;
      input.value = '';
    }
  };
  status.textContent = sessionStorage.getItem('parceljourney-capture-folder') || 'Select the capture Data folder';
})();
</script>
""";
    private static string S(JsonNode? n) => n is null ? "" : n.ToString();
    private static long N(JsonNode? n) => n is null ? 0 : n.GetValue<long>();
    private static string Stamp(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms).ToString("yyyy.MM.dd HH:mm:ss.fff");
    private static JsonObject Values(JsonObject e)
    {
        var result = new JsonObject();
        foreach (Match m in Regex.Matches(S(e["raw"]), @"\b(\w+)=([^,]*)(?:,|$)")) result[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('\'');
        foreach (var key in new[] { "Status", "TrackingId", "LastScanPos", "CarrierID", "ControlFlag", "OrderID", "CartonType", "PlcTarget", "WeightStart", "WeightBrutto", "GrossWeight", "VRNewHeight" }) if (e[key] is not null) result[key] = e[key]!.DeepClone();
        return result;
    }
    public static JsonObject Load(string selection, bool closedOnly = true)
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
            var paths = File.Exists(full) ? new[] {full} : Directory.EnumerateFiles(scanFolder,"*.pjourney",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
            foreach (string candidate in paths)
            {
                string path = SafePath(root, Path.GetRelativePath(root,candidate));
                try
                {
                    var parcel = PjourneyReader.Read(path, dictionary);
                    var indexed = index["entries"]!.AsArray().OfType<JsonObject>().FirstOrDefault(e=>SafePath(root,S(e["file"])).Equals(path,StringComparison.OrdinalIgnoreCase));
                    if(indexed is not null && S(indexed["parcel"])!=S(parcel["parcel"]))throw new InvalidDataException("Index/parcel identity mismatch.");
                    var metadata=parcel.DeepClone().AsObject();metadata.Remove("events");
                    bool isClosed = parcel["closed"]?.GetValue<bool>() == true;
                    bool startObserved = parcel["start_observed"]?.GetValue<bool>() == true;
                    if (closedOnly && !isClosed) continue;
                    var recorded = parcel["events"]!.AsArray().OfType<JsonObject>().OrderBy(e => N(e["t"])).ThenBy(e => S(e["source"])).ThenBy(e => N(e["offset"])).ToList();
                    if (recorded.Count == 0) throw new InvalidDataException("Parcel has no events.");
                    var recordsById = recorded.Where(r => S(r["record_id"]) != "").ToLookup(r => S(r["record_id"]));
                    int closeOrdinal = 0; var closeObservations = new JsonArray();
                    long first = N(parcel["first"]); var events = new JsonArray(); var observations = new JsonArray(); var stops = new JsonArray(); var legs = new JsonArray();
                    string location = "UNOBSERVED_POSITION"; JsonObject? previous = null;
                    if (S(recorded[0]["kind"]) != "scan") { if(!nodeMap.ContainsKey(location)){var placeholder=new JsonObject { ["id"]=location,["label"]="Position not observed",["x"]=2400,["y"]=2600,["kind"]="junction",["coordinateBasis"]="Unknown position placeholder; not physical equipment"};nodes.Add(placeholder);nodeMap[location]=placeholder;}previous=new JsonObject { ["id"]=location,["arrival"]=0d,["departure"]=0d};stops.Add(previous); } var state = new JsonObject();
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
                                    if (!edgeMap.ContainsKey(key))
                                    {
                                        var na = nodeMap[a]; var nb = nodeMap[observed];
                                        var edge = new JsonObject { ["id"] = key, ["a"] = a, ["b"] = observed, ["points"] = new JsonArray(new JsonArray(na["x"]!.DeepClone(), na["y"]!.DeepClone()), new JsonArray(na["x"]!.DeepClone(), nb["y"]!.DeepClone()), new JsonArray(nb["x"]!.DeepClone(), nb["y"]!.DeepClone())), ["basis"] = "Observed scanner succession; schematic connection, intermediate equipment unknown", ["topology"] = "observed-transition" }; edges.Add(edge); edgeMap[key] = edge;
                                    }
                                    edgeMap[key]["observationStatus"] = "Observed scanner succession in capture";
                                    double departure = previous["departure"]!.GetValue<double>();
                                    if (a != "UNOBSERVED_POSITION" && t > departure) legs.Add(new JsonObject { ["a"] = a, ["b"] = observed, ["start"] = departure, ["end"] = t, ["edges"] = new JsonArray(key), ["positionBasis"] = "Schematic interpolation between observed scans" });
                                }
                                previous = new JsonObject { ["id"] = observed, ["arrival"] = t, ["departure"] = t }; stops.Add(previous);
                            }
                            location = observed;
                        }
                        if (previous is not null && (observed == location || string.IsNullOrEmpty(observed))) previous["departure"] = t;
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
                    double duration = events.Last()!["t"]!.GetValue<double>(); previous!["departure"] = duration;
                    string line = stops.OfType<JsonObject>().Select(s => S(s["id"])).FirstOrDefault(s => s.StartsWith("SC_BWL"))?.Replace("SC_BWL", "") ?? "unknown";
                    var replay = new JsonObject { ["parcelId"] = S(parcel["parcel"]), ["visitKey"] = Path.GetFileNameWithoutExtension(path), ["closeCount"] = closeOrdinal, ["closeObservations"] = closeObservations, ["id"] = S(parcel["parcel"]), ["name"] = S(state["CarrierID"]) + " | line " + line + " | " + (startObserved ? "entry captured" : "entry unobserved") + " | " + Stamp(N(parcel["first"])), ["events"] = events, ["observations"] = observations, ["stops"] = stops, ["legs"] = legs, ["duration"] = duration, ["completeness"] = isClosed ? "CLOSED observed; " + closeOrdinal + " closure records; " + (startObserved ? "" : "entry not captured; ") + "capture coverage only" : "Partial capture; no CLOSED observed", ["outcome"] = isClosed ? "completed" : "partial", ["captureMetadata"] = metadata, ["fixture"] = new JsonObject(), ["source"] = "pjourney-capture", ["aliases"] = parcel["aliases"]?.DeepClone() ?? new JsonArray(), ["proof"] = new JsonObject { ["closedObserved"] = isClosed, ["startObserved"] = startObserved, ["completePhysicalCoverage"] = false, ["sourceFile"] = Path.GetFileName(path) } };
                    eventTotal += events.Count; parcels.Add(replay);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or InvalidOperationException or OverflowException) { errors.Add(new JsonObject { ["file"] = Path.GetFileName(path), ["error"] = ex.Message }); }
            }
            if (parcels.Count == 0) throw new InvalidDataException("No readable " + (closedOnly ? "CLOSED " : "") + "parcels found. " + errors.ToJsonString());
            var validation = new JsonObject { ["mode"] = "pjourney-capture", ["runs"] = parcels.Count, ["closedOnly"] = closedOnly, ["events"] = eventTotal, ["filesChecked"] = paths.Length, ["completeLifecycles"] = false, ["scope"] = "Parser-associated evidence only. CLOSED is a database observation; physical geometry and movement between scans are schematic.", ["unmappedScanners"] = new JsonArray(unknown.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["errors"] = errors };
            model["parcels"] = parcels; model["validation"] = validation; model.Remove("sourceManifest");
            current = model.ToJsonString();
            return validation;
        }
    }
    private static string SafePath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture index path outside Data.");
        return full;
    }
}
