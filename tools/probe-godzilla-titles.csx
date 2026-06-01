// Read-only: the STORED Title of the two Stern Godzilla bases — to check whether
// it carries the "(Pro)" / "(Premium/LE)" suffix the EditionResolver matches on.
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos;
using Azure.Identity;
using Newtonsoft.Json.Linq;

var c = new CosmosClient(
    "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",
    new DefaultAzureCredential(),
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
var m = c.GetContainer("pinwiz", "machines");

foreach (var id in new[] { "GweeP-MW95j", "GweeP-Ml9pZ" })
{
    var r = await m.ReadItemAsync<JObject>(id, new PartitionKey("stern"));
    string F(string k)
    {
        var p = r.Resource.Properties().FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
        return p?.Value?.ToString() ?? "";
    }
    Console.WriteLine($"{id}: Title='{F("title")}'");
    var ed = F("editions");
    if (!string.IsNullOrEmpty(ed))
        Console.WriteLine($"    editions(120)={ed.Replace("\r", "").Replace("\n", " ").Substring(0, Math.Min(120, ed.Length))}");
}
