using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ParcelLogMiddleware
{
    // The same v2 contract used by compact_v2.py and ParcelJourney.Core.
    internal static class CompactWriter
    {
        public static void Write(string path, object snapshot)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var source = (Dictionary<string, object>)json.DeserializeObject(json.Serialize(snapshot));
            var key = source.Keys.First(k => k.Equals("events", StringComparison.OrdinalIgnoreCase));
            var events = ((IEnumerable)source[key]).Cast<Dictionary<string, object>>().ToArray();
            var columns = events.SelectMany(e => e.Keys).Distinct().ToArray();
            var dictionaries = columns.Select(c => new List<object>()).ToArray();
            var indexes = columns.Select(c => new Dictionary<string, int>()).ToArray();
            var rows = new List<int[]>();
            foreach (var e in events)
            {
                var row = new int[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                {
                    object value;
                    if (!e.TryGetValue(columns[i], out value)) { row[i] = -1; continue; }
                    var token = json.Serialize(value);
                    int index;
                    if (!indexes[i].TryGetValue(token, out index))
                    { index = dictionaries[i].Count; indexes[i][token] = index; dictionaries[i].Add(value); }
                    row[i] = index;
                }
                rows.Add(row);
            }
            source.Remove(key);
            var encoded = json.Serialize(new { version = 2, metadata = source, eventKey = key, columns, dict = dictionaries, rows });
            var temp = path + ".tmp";
            using (var file = File.Create(temp))
            using (var zip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new StreamWriter(zip, new UTF8Encoding(false))) writer.Write(encoded);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
}
