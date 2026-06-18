#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
// One-shot migration (2026-06-10): rewrite stored opdbSourceUrl values from
// the broken https://opdb.org/machines/{opdb_id} scheme (404s — opdb.org
// machine pages use internal numeric ids) to the durable
// https://opdb.org/search?q={opdb_id} deep link. Covers the machine-level
// field and every editions[].opdbSourceUrl. Idempotent: already-migrated
// rows are skipped. Run --sync-metadata-cards afterwards to refresh the
// AI Search metadata cards that embed these URLs.
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;

var c = new CosmosClient(
    "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",
    new DefaultAzureCredential(),
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
var machines = c.GetContainer("pinwiz", "machines");

const string brokenPrefix = "https://opdb.org/machines/";
string Fixed(string url) =>
    "https://opdb.org/search?q=" + Uri.EscapeDataString(url[brokenPrefix.Length..]);

var scanned = 0; var updated = 0;
var q = new QueryDefinition("SELECT * FROM c");
var it = machines.GetItemQueryIterator<JObject>(q);
while (it.HasMoreResults)
{
    foreach (var doc in await it.ReadNextAsync())
    {
        scanned++;
        var changed = false;

        var url = (string?)doc["opdbSourceUrl"];
        if (url is not null && url.StartsWith(brokenPrefix, StringComparison.Ordinal))
        {
            doc["opdbSourceUrl"] = Fixed(url);
            changed = true;
        }

        if (doc["editions"] is JArray editions)
        {
            foreach (var ed in editions.OfType<JObject>())
            {
                var edUrl = (string?)ed["opdbSourceUrl"];
                if (edUrl is not null && edUrl.StartsWith(brokenPrefix, StringComparison.Ordinal))
                {
                    ed["opdbSourceUrl"] = Fixed(edUrl);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            var pk = (string)doc["manufacturer"]!; // /manufacturer is the partition key path
            await machines.ReplaceItemAsync(doc, (string)doc["id"]!, new PartitionKey(pk));
            updated++;
            if (updated % 100 == 0) Console.WriteLine($"updated {updated} (scanned {scanned})...");
        }
    }
}
Console.WriteLine($"Done. Scanned {scanned} machine docs; rewrote URLs on {updated}.");
