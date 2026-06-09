#nullable enable
// Step 2 (AB#259 migration): delete the 3 stale Sega Godzilla rows from
// scraped_documents. These are Stern manuals wrongly linked to Sega's
// G5po2-MeP6B. Point-delete ONLY these 3 ids in partition G5po2-MeP6B.
// Defensive: READ each row first, assert machine_id==G5po2-MeP6B before deleting,
// then re-read to confirm it's gone. Touches nothing else.
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos;
using Azure.Identity;
using Newtonsoft.Json.Linq;

const string Pk = "G5po2-MeP6B";
var ids = new[] { "doc_b1d3a60ec154d328", "doc_58c56c2ec9dfb4df", "doc_6e235388cf0e319c" };

var c = new CosmosClient(
    "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",
    new DefaultAzureCredential(),
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
var docs = c.GetContainer("pinwiz", "scraped_documents");

foreach (var id in ids)
{
    try
    {
        // 1. Read + assert it is the stale Sega row we intend to delete.
        var r = await docs.ReadItemAsync<JObject>(id, new PartitionKey(Pk));
        var mid = r.Resource["machine_id"]?.ToString();
        var title = r.Resource["machine_title"]?.ToString();
        if (mid != Pk)
        {
            Console.WriteLine($"SKIP {id}: machine_id='{mid}' != '{Pk}' — NOT deleting (unexpected).");
            continue;
        }
        Console.WriteLine($"DELETE {id} (machine_id={mid}, title='{title}')...");

        // 2. Delete.
        await docs.DeleteItemAsync<JObject>(id, new PartitionKey(Pk));

        // 3. Confirm gone.
        try
        {
            await docs.ReadItemAsync<JObject>(id, new PartitionKey(Pk));
            Console.WriteLine($"  WARNING: {id} still readable after delete!");
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine($"  OK: {id} confirmed deleted (404 on re-read).");
        }
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine($"SKIP {id}: already absent (404).");
    }
}
Console.WriteLine("Done.");
