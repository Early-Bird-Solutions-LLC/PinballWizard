#!/usr/bin/env dotnet-script
// Read-only probe: machine-catalog ground truth for the AB#259 linker-slug investigation.
// Answers: how many of the ~2,158 machines actually carry a non-empty ManufacturerSlugs map
// (the linker's only matching key), broken down by manufacturer, plus the Godzilla rows.
//
// Usage (live, AAD via DefaultAzureCredential — needs `az login` on sub b1f33f17):
//   $env:COSMOS_ENDPOINT="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
//   dotnet script tools/probe-machine-slugs.csx

#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"

using Microsoft.Azure.Cosmos;
using Azure.Identity;

var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/";
var dbName = Environment.GetEnvironmentVariable("COSMOS_DB") ?? "pinwiz";

// Gateway mode forces the query plan to be retrieved from the Cosmos gateway rather than
// generated locally via the native ServiceInterop DLL, which throws 0x800A0B00 on this
// .NET 10 box. Read-only probe — gateway latency is irrelevant here.
var client = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
});
var machines = client.GetContainer(dbName, "machines");

// Cosmos NoSQL has no object-key-count function; stream a small projection and tally client-side.
long total = 0, withSlugs = 0, withoutSlugs = 0;
var withByPk = new Dictionary<string, long>();
var totalByPk = new Dictionary<string, long>();
var slugKeyHistogram = new Dictionary<string, long>();

using (var it = machines.GetItemQueryIterator<dynamic>(new QueryDefinition(
    "SELECT c.PartitionKey, c.ManufacturerSlugs FROM c")))
{
    while (it.HasMoreResults)
        foreach (var r in await it.ReadNextAsync())
        {
            total++;
            string pk = (string)(r.PartitionKey ?? "<null>");
            totalByPk[pk] = totalByPk.GetValueOrDefault(pk) + 1;

            bool has = false;
            var slugs = r.ManufacturerSlugs;
            if (slugs != null)
            {
                foreach (var prop in (IEnumerable<dynamic>)slugs) // Newtonsoft JObject → JProperty
                {
                    string key = prop.Name;
                    string val = prop.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        has = true;
                        slugKeyHistogram[key] = slugKeyHistogram.GetValueOrDefault(key) + 1;
                    }
                }
            }
            if (has) { withSlugs++; withByPk[pk] = withByPk.GetValueOrDefault(pk) + 1; }
            else withoutSlugs++;
        }
}

Console.WriteLine($"machines total:                            {total}");
Console.WriteLine($"machines WITH non-empty ManufacturerSlugs: {withSlugs}");
Console.WriteLine($"machines WITHOUT:                          {withoutSlugs}");

Console.WriteLine("--- with-slugs by PartitionKey ---");
foreach (var kv in withByPk.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-22} {kv.Value}");

Console.WriteLine("--- slug-key histogram (which mfr key holds the slug) ---");
foreach (var kv in slugKeyHistogram.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-22} {kv.Value}");

Console.WriteLine("--- total by PartitionKey (manufacturer) ---");
foreach (var kv in totalByPk.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-22} {kv.Value}");

Console.WriteLine("--- Godzilla machines (id / pk / title / ManufacturerSlugs) ---");
using (var it = machines.GetItemQueryIterator<dynamic>(new QueryDefinition(
    "SELECT c.id, c.PartitionKey, c.Title, c.ManufacturerSlugs FROM c WHERE CONTAINS(LOWER(c.Title), 'godzilla')")))
{
    while (it.HasMoreResults)
        foreach (var r in await it.ReadNextAsync())
        {
            string slugs = r.ManufacturerSlugs == null
                ? "<null>"
                : ((object)r.ManufacturerSlugs).ToString().Replace("\r", "").Replace("\n", " ");
            Console.WriteLine($"  {(string)r.id,-18} pk={(string)r.PartitionKey,-10} '{(string)r.Title}'");
            Console.WriteLine($"      slugs={slugs}");
        }
}
