using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ParcelLogMiddleware
{
    internal sealed class JourneyEvent
    {
        public string EventId { get; set; } public string Timestamp { get; set; } public string EventType { get; set; } public string Layer { get; set; } public string System { get; set; } public string Phase { get; set; } public string Location { get; set; } public string LocationConfidence { get; set; }
        public string ParcelId { get; set; } public string OrderId { get; set; } public string TrackingId { get; set; } public string Target { get; set; } public string Status { get; set; } public string Raw { get; set; } public string SourceFile { get; set; }
        public int Line { get; set; }
        public string[] Identifiers { get; set; }
        public List<JourneyEvidence> Evidence { get; set; } = new List<JourneyEvidence>();
    }
    internal sealed class JourneyEvidence { public string SourceFile { get; set; } public string Layer { get; set; } public string System { get; set; } public string Raw { get; set; } public int Line { get; set; } }
    internal sealed class ParcelSnapshot { public string ParcelId { get; set; } public string[] Aliases { get; set; } public string Status { get; set; } public bool IsLive { get; set; } public string FirstSeen { get; set; } public string LastSeen { get; set; } public List<JourneyEvent> Events { get; set; } public string[] SourcePartitions { get; set; } public string Completeness { get; set; } }

    internal static class Program
    {
        static readonly Regex Stamp = new Regex(@"^(\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d\.\d{3})", RegexOptions.Compiled);
        static readonly Regex Db = new Regex(@"(?:INSERT|UPDATE) \(tudata\) (?<x>.*)", RegexOptions.Compiled);
        static readonly Regex Scanner = new Regex(@"(?:OnScannerData|OnPlcAcknowledge): received Data: '([^']+)'", RegexOptions.Compiled);
        static readonly Regex Field = new Regex(@"(?<k>\w+)=([^,]*),", RegexOptions.Compiled);
        static readonly Regex PackedZp = new Regex(@"ZP\s+(?<order>\d{10})(?<parcel>\d{10})", RegexOptions.Compiled);
        static readonly Regex ParcelField = new Regex(@"ParcelId\s*='?(?<id>\d{10})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex TrackingField = new Regex(@"TrackingI[Dd]\s*='?(?<id>\d{10,24})", RegexOptions.Compiled);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly object Gate = new object();
        static readonly Dictionary<string, List<JourneyEvent>> Index = new Dictionary<string, List<JourneyEvent>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, long> Offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> LineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static volatile string Status = "Starting";
        static volatile int FilesSeen, EventsSeen;

        static void Main(string[] args)
        {
            var logs = Arg(args, "--logs", null); var output = Arg(args, "--output", "output");
            var port = int.Parse(Arg(args, "--port", "4816"));
            if (logs == null || !Directory.Exists(logs)) { Console.WriteLine("Usage: ParcelLogMiddleware.exe --logs <folder> [--output <folder>] [--port <port>]"); return; }
            Directory.CreateDirectory(output);
            Task.Factory.StartNew(() => Watch(logs, output), TaskCreationOptions.LongRunning);
            Console.WriteLine("UI: http://localhost:" + port + "/");
            Serve(port);
        }

        static void Watch(string root, string output)
        {
            Status = "Indexing";
            while (true)
            {
                foreach (var file in Directory.GetFiles(root, "*.log", SearchOption.AllDirectories)) ScanFile(file, output);
                MaterializeParcels(output);
                Status = "Watching";
                Thread.Sleep(3000);
            }
        }

        static void ScanFile(string file, string output)
        {
            var layer = Layer(file); var system = Path.GetFileName(file).Split('_')[0];
            long offset = 0; lock (Gate) { Offsets.TryGetValue(file, out offset); }
            var length = new FileInfo(file).Length; if (length < offset) { offset = 0; Pending.Remove(file); LineCounts.Remove(file); }
            if (length == offset) return;
            try
            {
                byte[] bytes; using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(offset, SeekOrigin.Begin); bytes = new byte[Math.Min(length - offset, 262144)]; var read = fs.Read(bytes, 0, bytes.Length); if (read != bytes.Length) Array.Resize(ref bytes, read);
                }
                string text = Encoding.Default.GetString(bytes); string pending; lock (Gate) { Pending.TryGetValue(file, out pending); }
                text = (pending ?? "") + text; var complete = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None); var hasFinalNewline = text.EndsWith("\n", StringComparison.Ordinal); var final = hasFinalNewline ? "" : complete[complete.Length - 1];
                var count = complete.Length - 1;
                int previousLines; LineCounts.TryGetValue(file, out previousLines);
                for (var i = 0; i < count; i++)
                    {
                    var line = complete[i]; var lineNo = previousLines + i + 1; var sm = Stamp.Match(line); if (!sm.Success) continue;
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string parcel = null, order = null, tracking = null, type = "LogMessage", phase = null, target = null;
                    var db = Db.Match(line); if (db.Success) { var fields = Field.Matches(db.Groups["x"].Value).Cast<Match>().ToDictionary(m => m.Groups["k"].Value, m => m.Value.Substring(m.Value.IndexOf('=') + 1).TrimEnd(',')); fields.TryGetValue("ParcelID", out parcel); fields.TryGetValue("OrderID", out order); fields.TryGetValue("TrackingId", out tracking); fields.TryGetValue("PlcTarget", out target); type = "DatabaseState"; }
                    var scan = Scanner.Match(line); if (scan.Success) { var p = scan.Groups[1].Value.Split('|'); if (p.Length >= 3) { phase = p[1]; tracking = p[2].Trim(); var packedScan = PackedZp.Match(tracking); if (packedScan.Success) { order = packedScan.Groups["order"].Value; parcel = packedScan.Groups["parcel"].Value; tracking = null; } if (p.Length > 3) target = p[3].Trim(); type = line.Contains("OnPlcAcknowledge") ? "PlcAcknowledgement" : "ScannerRead"; } }
                    var packed = PackedZp.Match(line); if (packed.Success) { ids.Add(packed.Groups["order"].Value); ids.Add(packed.Groups["parcel"].Value); if (order == null) order = packed.Groups["order"].Value; if (parcel == null) parcel = packed.Groups["parcel"].Value; }
                    var pf = ParcelField.Match(line); if (pf.Success) { ids.Add(pf.Groups["id"].Value); if (parcel == null) parcel = pf.Groups["id"].Value; }
                    var tf = TrackingField.Match(line); if (tf.Success) { ids.Add(tf.Groups["id"].Value); if (tracking == null) tracking = tf.Groups["id"].Value; }
                    if (parcel != null) ids.Add(parcel); if (order != null) ids.Add(order); if (!String.IsNullOrEmpty(tracking) && tracking != "NOREAD") ids.Add(tracking);
                    if (ids.Count == 0) continue;
                    var e = new JourneyEvent { EventId = file + ":" + lineNo, Timestamp = sm.Groups[1].Value, EventType = type, Layer = layer, System = system, Phase = phase, Location = phase, LocationConfidence = phase == null ? "Unknown" : "Explicit", ParcelId = parcel, OrderId = order, TrackingId = tracking, Target = target, Raw = line, SourceFile = file, Line = lineNo, Identifiers = ids.ToArray() };
                    if (db.Success) { e.Status = Regex.Match(line, @"\bStatus=([^,]*)").Groups[1].Value; e.Location = Regex.Match(line, @"\bLastScanPos=([^,]*)").Groups[1].Value; }
                    if (!String.IsNullOrEmpty(parcel)) Dirty.Add(parcel);
                    foreach (var id in ids) { List<JourneyEvent> list; lock (Gate) { if (!Index.TryGetValue(id, out list)) Index[id] = list = new List<JourneyEvent>(); list.Add(e); } }
                    EventsSeen++;
                    AppendEvent(output, e);
                    }
                lock (Gate) { Offsets[file] = offset + bytes.Length; LineCounts[file] = previousLines + count; if (String.IsNullOrEmpty(final)) Pending.Remove(file); else Pending[file] = final; FilesSeen++; }
                PersistState(output);
            }
            catch (IOException) { }
        }

        static void PersistState(string output) { try { File.WriteAllText(Path.Combine(output, "reader-state.json"), Json.Serialize(new { status = Status, files = FilesSeen, events = EventsSeen, updatedUtc = DateTime.UtcNow })); } catch (IOException) { } }
        static void AppendEvent(string output, JourneyEvent e) { try { var dir = Path.Combine(output, "events"); Directory.CreateDirectory(dir); var day = e.Timestamp.Substring(0, 10).Replace('.', '-'); File.AppendAllText(Path.Combine(dir, day + ".ndjson"), Json.Serialize(e) + Environment.NewLine, Encoding.UTF8); } catch (IOException) { } }
        static void MaterializeParcels(string output)
        {
            Dictionary<string, List<JourneyEvent>> snapshots = new Dictionary<string, List<JourneyEvent>>();
            lock (Gate)
            {
                foreach (var pair in Index.Where(p => Dirty.Contains(p.Key) && p.Value.Any(e => e.ParcelId == p.Key)))
                {
                    var events = pair.Value.GroupBy(e => e.EventId).Select(g => g.First()).OrderBy(e => e.Timestamp).ToList();
                    if (events.Count == 0) continue; snapshots[pair.Key] = events;
                }
            }
            var dir = Path.Combine(output, "parcels"); Directory.CreateDirectory(dir);
            foreach (var pair in snapshots)
            {
                var events = pair.Value; var aliases = events.SelectMany(e => e.Identifiers ?? new string[0]).Where(x => !String.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
                var snap = new ParcelSnapshot { ParcelId = pair.Key, Aliases = aliases, Status = events.LastOrDefault(e => e.EventType == "DatabaseState") == null ? "Observed" : "Mapped", IsLive = Status != "Complete", FirstSeen = events[0].Timestamp, LastSeen = events[events.Count - 1].Timestamp, Events = events, SourcePartitions = events.Select(e => Partition(e.SourceFile)).Distinct().ToArray(), Completeness = Status == "Watching" ? "Live" : "Complete" };
                var tmp = Path.Combine(dir, pair.Key + ".json.tmp"); File.WriteAllText(tmp, Json.Serialize(snap), Encoding.UTF8); var final = Path.Combine(dir, pair.Key + ".json"); if (File.Exists(final)) File.Replace(tmp, final, null); else File.Move(tmp, final);
                CompactWriter.Write(Path.Combine(dir, pair.Key + ".pj.json.gz"), snap);
                Dirty.Remove(pair.Key);
            }
        }
        static string Partition(string path) { var d = Path.GetDirectoryName(path); var n = Path.GetFileName(d); return Regex.IsMatch(n ?? "", @"^KW_\d+$", RegexOptions.IgnoreCase) ? n : "root"; }

        static string Layer(string file) { var n = Path.GetFileName(file); if (n.StartsWith("Con")) return "Transport"; if (n.StartsWith("ProcPLC")) return "PLC"; if (n.StartsWith("ProcLogic")) return "Logic"; if (n.StartsWith("ProcLVS")) return "LVS"; if (n.StartsWith("ProcEtikettierer")) return "Labeler"; if (n.StartsWith("ProcCamera")) return "Camera"; if (n.StartsWith("RemoteManagement")) return "UI"; return "Other"; }
        static string Arg(string[] a, string key, string fallback) { var i = Array.IndexOf(a, key); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
        static void Serve(int port)
        {
            var h = new HttpListener(); h.Prefixes.Add("http://localhost:" + port + "/"); h.Start();
            while (true) { var c = h.GetContext(); var q = c.Request.QueryString["q"]; if (q == null) { var html = Encoding.UTF8.GetBytes(Page()); c.Response.ContentType = "text/html; charset=utf-8"; c.Response.OutputStream.Write(html, 0, html.Length); c.Response.Close(); continue; } object body = Find(q); var bytes = Encoding.UTF8.GetBytes(Json.Serialize(body)); c.Response.ContentType = "application/json; charset=utf-8"; c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close(); }
        }
        static string Page() { return @"<!doctype html><meta charset=utf-8><title>Parcel Log Explorer</title><style>body{font:14px Segoe UI;margin:24px;background:#10151c;color:#e8edf2}input,button{font:16px;padding:9px;background:#18232d;color:white;border:1px solid #456}button{cursor:pointer}.event{margin:12px 0;padding:12px;border-left:5px solid #4aa3df;background:#18232d}.PLC{border-color:#e58b45}.Transport{border-color:#a77be8}.LVS{border-color:#55c98a}.Labeler{border-color:#e3c34e}.meta{color:#aebdca}.raw{white-space:pre-wrap;color:#c8d0d8;font:12px Consolas}details{margin-top:8px}</style><h1>Parcel Log Explorer</h1><input id=q placeholder='Parcel ID, tracking ID, order ID...' size=45><button onclick=go()>Search</button><div id=out></div><script>q.onkeydown=e=>e.key==='Enter'&&go();async function go(){let v=q.value.trim();if(!v)return;let d=await fetch('/?q='+encodeURIComponent(v)).then(r=>r.json());out.innerHTML='<h2>'+d.eventCount+' correlated events</h2><p>Reader: '+d.status+' · files: '+d.files+' · events: '+d.indexedEvents+'</p>'+d.events.map(e=>'<div class=event '+e.Layer+'><b>'+e.Timestamp+'</b> · '+e.EventType+' · '+e.Layer+' / '+(e.System||'')+'</div><div class=meta>location: '+(e.Location||'')+' · target: '+(e.Target||'')+' · '+e.SourceFile+':'+e.Line+'</div><details><summary>raw evidence</summary><div class=raw>'+esc(e.Raw)+'</div></details></div>').join('')}function esc(s){return (s||'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]))}</script>"; }
        static object Find(string q)
        {
            List<JourneyEvent> hits; lock (Gate) { hits = Index.Where(p => p.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).SelectMany(p => p.Value).GroupBy(e => e.EventId).Select(g => g.First()).OrderBy(e => e.Timestamp).ToList(); }
            return new { query = q, status = Status, files = FilesSeen, indexedEvents = EventsSeen, events = hits, eventCount = hits.Count };
        }
    }
}
