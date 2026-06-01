// Read-only POINT READs (no query plan — bypasses the .NET 10 ServiceInterop bug)
// of the two Stern Godzilla base machines, to confirm whether the reconcile wrote
// ManufacturerSlugs['stern']='godzilla' to each. Usage:
//   dotnet script tools/probe-godzilla-slugs.csx
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos;
using Azure.Identity;
using Newtonsoft.Json.Linq;

var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/";
var db = Environment.GetEnvironmentVariable("COSMOS_DB") ?? "pinwiz";
var client = new CosmosClient(endpoint, new DefaultAzureCredential(),
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
var machines = client.GetContainer(db, "machines");

// (id, partitionKey=manufacturer)
var targets = new (string Id, string Pk, string Label)[]
{
    ("GweeP-MW95j", "stern", "Godzilla (Pro)"),
    ("GweeP-Ml9pZ", "stern", "Godzilla (Premium/LE)"),
};

foreach (var (id, pk, label) in targets)
{
    try
    {
        var resp = await machines.ReadItemAsync<JObject>(id, new PartitionKey(pk));
        var m = resp.Resource;
        // Find the slug field case-insensitively.
        var slugProp = m.Properties().FirstOrDefault(p =>
            p.Name.Equals("manufacturerSlugs", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("ManufacturerSlugs", StringComparison.OrdinalIgnoreCase));
        var groupProp = m.Properties().FirstOrDefault(p => p.Name.Equals("groupId", StringComparison.OrdinalIgnoreCase));
        var yearProp = m.Properties().FirstOrDefault(p => p.Name.Equals("year", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{id} [{label}]  group={groupProp?.Value}  year={yearProp?.Value}");
        Console.WriteLine($"    slugs = {(slugProp?.Value?.ToString() ?? "<field-absent>").Replace("\r","").Replace("\n"," ")}");
    }
    catch (CosmosException ex)
    {
        Console.WriteLine($"{id} [{label}]  READ FAILED: {ex.StatusCode}");
    }
}
