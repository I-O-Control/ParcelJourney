using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ParcelJourney.Core;

/// <summary>Reads the parser's PJ01 binary contract, not the older compact JSON demo.</summary>
public static class PjourneyReader
{
    /// <summary>Metadata only; full payload checksum validation still happens on Read.</summary>
    public static JsonObject ReadHeader(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("Parcel file limit.");
        var envelope = new byte[37]; file.ReadExactly(envelope);
        if (Encoding.ASCII.GetString(envelope, 0, 4) != "PJ01" || envelope[4] > 1) throw new InvalidDataException("Unsupported .pjourney header.");
        using Stream payload = envelope[4] == 1 ? new ZLibStream(file, CompressionMode.Decompress, leaveOpen: true) : file;
        long length = 0; bool complete = false;
        for (int shift = 0; shift < 63; shift += 7)
        {
            int b = payload.ReadByte(); if (b < 0) throw new InvalidDataException("Truncated header.");
            length |= (long)(b & 127) << shift;
            if (b < 128) { complete = true; break; }
        }
        if (!complete || length is < 1 or > 256 * 1024) throw new InvalidDataException("Parcel metadata limit.");
        var bytes = new byte[(int)length]; payload.ReadExactly(bytes);
        return JsonNode.Parse(bytes)!.AsObject();
    }

    public static JsonObject Read(string path, IReadOnlyList<string> dictionary)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Parcel file limit.");
        var file = File.ReadAllBytes(path);
        if (file.Length < 37 || Encoding.ASCII.GetString(file, 0, 4) != "PJ01" || file[4] > 1)
            throw new InvalidDataException("Unsupported .pjourney header.");
        var payload = file.AsSpan(37).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), file.AsSpan(5, 32)))
            throw new InvalidDataException("Parcel checksum mismatch.");
        byte[] body = payload;
        if (file[4] == 1)
        {
            using var input = new MemoryStream(payload);
            using var zip = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = zip.Read(buffer)) > 0)
            {
                if (output.Length + count > 32 * 1024 * 1024) throw new InvalidDataException("Parcel decompression limit.");
                output.Write(buffer, 0, count);
            }
            body = output.ToArray();
        }
        using var data = new MemoryStream(body);
        byte[] Exact(int n) { if (n < 0 || n > data.Length - data.Position) throw new InvalidDataException("Truncated parcel."); var b = new byte[n]; data.ReadExactly(b); return b; }
        long Var()
        {
            ulong value = 0;
            for (int shift = 0; shift < 63; shift += 7)
            {
                int b = data.ReadByte(); if (b < 0) throw new InvalidDataException("Truncated varint.");
                value |= (ulong)(b & 127) << shift;
                if (b < 128) return checked((long)value);
            }
            throw new InvalidDataException("Invalid varint.");
        }
        string Symbol(long n) => n >= 0 && n < dictionary.Count ? dictionary[(int)n] : throw new InvalidDataException("Missing shared dictionary symbol.");
        var parcel = JsonNode.Parse(Exact(checked((int)Var())))!.AsObject();
        if (parcel.ContainsKey("events")) throw new InvalidDataException("Unexpected events in header.");
        long eventCount = Var(), time = 0;
        if (eventCount > 100000) throw new InvalidDataException("Parcel event limit.");
        var events = new JsonArray();
        for (long i = 0; i < eventCount; i++)
        {
            time = checked(time + Var()); var e = new JsonObject { ["t"] = time };
            long fields = Var(); if (fields > 256) throw new InvalidDataException("Event field limit.");
            for (long j = 0; j < fields; j++)
            {
                string key = Symbol(Var()); if (e.ContainsKey(key)) throw new InvalidDataException("Duplicate event field: " + key); int tag = data.ReadByte(); long value = Var();
                e[key] = tag switch { 0 => JsonValue.Create(value), 1 => JsonValue.Create(Symbol(value)), 2 => JsonValue.Create(new UTF8Encoding(false, true).GetString(Exact(checked((int)value)))), _ => throw new InvalidDataException("Unknown field encoding.") };
            }
            events.Add(e);
        }
        if (data.Position != data.Length) throw new InvalidDataException("Trailing parcel data.");
        parcel["events"] = events;
        return parcel;
    }

    public static IReadOnlyList<string> ReadDictionary(string path, string expectedHash)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var values = doc["values"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        // Parser hashes canonical JSON with literal UTF-8 Unicode.
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values, options)))).ToLowerInvariant();
        if (hash != expectedHash || doc["id"]!.GetValue<string>() != expectedHash) throw new InvalidDataException("Shared dictionary checksum mismatch.");
        return values;
    }
}
