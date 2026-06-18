using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

var endpoint = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/";
var credential = new DefaultAzureCredential();
using var cosmosClient = new CosmosClient(endpoint, credential, new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
});

var container = cosmosClient.GetDatabase("pinwiz").GetContainer("machine_title_lookups");

var query = new QueryDefinition("SELECT c.id, c.opdbIds, c.manufacturers FROM c");
var feedOptions = new QueryRequestOptions { MaxItemCount = 500 };
var feed = container.GetItemQueryIterator<JsonElement>(query, requestOptions: feedOptions);

var collisionTitles = new List<string>();
var collisionOpdbIds = new List<string[]>();
var collisionManufacturers = new List<string[]>();
int totalRows = 0;

while (feed.HasMoreResults)
{
    var page = await feed.ReadNextAsync();
    foreach (var item in page)
    {
        totalRows++;
        if (!item.TryGetProperty("opdbIds", out var opdbProp)) continue;
        var opdbIds = opdbProp.EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (opdbIds.Length <= 1) continue;

        var title = item.GetProperty("id").GetString()!;
        var manufacturers = item.GetProperty("manufacturers").EnumerateArray()
            .Select(x => x.GetString()!)
            .ToArray();
        collisionTitles.Add(title);
        collisionOpdbIds.Add(opdbIds);
        collisionManufacturers.Add(manufacturers);
    }
}

Console.WriteLine($"Total lookup rows scanned: {totalRows}");
Console.WriteLine($"=== Title collision rows (opdbIds.Length > 1): {collisionTitles.Count} ===");
Console.WriteLine();

var grouped = new Dictionary<string, List<int>>();
for (var i = 0; i < collisionTitles.Count; i++)
{
    var key = string.Join(" vs ", collisionManufacturers[i].OrderBy(m => m));
    if (!grouped.ContainsKey(key)) grouped[key] = new List<int>();
    grouped[key].Add(i);
}

Console.WriteLine("=== By manufacturer collision pattern ===");
foreach (var kvp in grouped.OrderByDescending(x => x.Value.Count))
{
    Console.WriteLine($"  [{kvp.Key}] — {kvp.Value.Count} title(s)");
    foreach (var idx in kvp.Value.OrderBy(i => collisionTitles[i]))
        Console.WriteLine($"    \"{collisionTitles[idx]}\"  opdbIds: [{string.Join(", ", collisionOpdbIds[idx])}]");
}

Console.WriteLine();
Console.WriteLine("=== Same-manufacturer collisions (edition dedup failure) ===");
var hasSame = false;
for (var i = 0; i < collisionTitles.Count; i++)
{
    if (collisionManufacturers[i].Distinct().Count() < collisionManufacturers[i].Length)
    {
        hasSame = true;
        Console.WriteLine($"  \"{collisionTitles[i]}\" — manufacturers: [{string.Join(", ", collisionManufacturers[i])}]  opdbIds: [{string.Join(", ", collisionOpdbIds[i])}]");
    }
}
if (!hasSame) Console.WriteLine("  None found.");

Console.WriteLine();
Console.WriteLine("=== All collision titles (alphabetical) ===");
foreach (var idx in Enumerable.Range(0, collisionTitles.Count).OrderBy(i => collisionTitles[i]))
{
    var pairs = collisionOpdbIds[idx].Zip(collisionManufacturers[idx], (o, m) => $"{m}:{o}");
    Console.WriteLine($"  \"{collisionTitles[idx]}\" → {string.Join(" | ", pairs)}");
}
