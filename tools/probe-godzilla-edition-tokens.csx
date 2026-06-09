// Read-only: the stored editionLabel + editionTokens of the two Stern Godzilla bases.
// This is the field the OPDB re-sync (Task 10 Step 1) populates and the Step 1 gate
// checks: GweeP-MW95j -> ["pro"], GweeP-Ml9pZ -> ["premium","le","70th"], Title both "Godzilla".
// Point-read only (ad-hoc aggregate queries throw 0x800A0B00 on this .NET 10 box).
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
    try
    {
        var r = await m.ReadItemAsync<JObject>(id, new PartitionKey("stern"));
        string F(string k)
        {
            var p = r.Resource.Properties().FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
            return p?.Value?.ToString()?.Replace("\r", "").Replace("\n", " ") ?? "<field-absent>";
        }
        Console.WriteLine($"{id}: Title='{F("title")}'");
        Console.WriteLine($"    editionLabel = {F("editionLabel")}");
        Console.WriteLine($"    editionTokens = {F("editionTokens")}");
    }
    catch (CosmosException ex)
    {
        Console.WriteLine($"{id}: READ FAILED {ex.StatusCode}");
    }
}
