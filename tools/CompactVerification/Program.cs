using System.Text.Json;
using ParcelJourney.Core;

var root = Path.GetFullPath(args[0]);
using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"analysis/compact-real-10.json")));
var count = 0;
foreach(var feed in document.RootElement.GetProperty("parcels").EnumerateArray())
{
    var decoded=CompactJourneyReader.Decode(feed);
    var id=decoded["parcelId"]!.GetValue<string>();
    var original=File.ReadAllText(Path.Combine(root,"real-feed/parcels",id+".json"));
    if(!System.Text.Json.Nodes.JsonNode.DeepEquals(decoded,System.Text.Json.Nodes.JsonNode.Parse(original)))throw new Exception("Field mismatch: "+id);
    var expected=RealDatasetJourneyReader.FromJson(original,id);
    var actual=await CompactJourneyReader.TryReadAsync(Path.Combine(root,"analysis/compact-parcels"),id,default);
    if(JsonSerializer.Serialize(expected)!=JsonSerializer.Serialize(actual))throw new Exception("Journey mismatch: "+id);
    count++;
}
if(await RealDatasetJourneyReader.TryReadAsync(Path.Combine(root,"real-feed"),"DOES-NOT-EXIST",default)!=null)throw new Exception("Unknown ID returned another parcel");
Console.WriteLine($"PASS: {count} parcels, all JSON values and reconstructed journey fields match; unknown search does not return another parcel.");
