#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos;
using Azure.Identity;
using Newtonsoft.Json.Linq;

// CORRECT normalizer = MachineTitleLookup.NormalizeTitle: lowercase + replace
// only / \ ? # with '_'. Keeps spaces — the earlier probe wrongly stripped them.
static string Norm(string t)
{
    var lowered = t.Trim().ToLowerInvariant();
    var chars = lowered.Select(c =>
    {
        if (c == '/' || c == '\\' || c == '?' || c == '#') return '_';
        return c;
    }).ToArray();
    return new string(chars);
}

var c = new CosmosClient(
    "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",
    new DefaultAzureCredential(),
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
var lk = c.GetContainer("pinwiz", "machine_title_lookups");

foreach (var q in new[] { "Godzilla", "Godzilla Pro", "Godzilla Premium", "Godzilla LE", "Godzilla 70th" })
{
    var k = Norm(q);
    try
    {
        var r = await lk.ReadItemAsync<JObject>(k, new PartitionKey(k));
        var ids = r.Resource["opdbIds"]?.ToString()?.Replace("\r", "").Replace("\n", " ") ?? "?";
        Console.WriteLine($"getMachineByTitle(\"{q}\")  key='{k}'  -> opdbIds={ids}");
    }
    catch (CosmosException ex)
    {
        Console.WriteLine($"getMachineByTitle(\"{q}\")  key='{k}'  -> NOT FOUND ({ex.StatusCode})");
    }
}
