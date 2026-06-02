// Read-only: query the live scraped_documents for Godzilla docs and show which
// machine_id each linked to — the gate-G4 headline check.
//   Godzilla_Pro_web.pdf  → expect GweeP-MW95j (Pro)
//   Godzilla_LE_Pre_web.pdf → expect GweeP-Ml9pZ (Premium/LE)
//   rulesheet / feature-matrix → expect BOTH (group fan-out)
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
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, AllowBulkExecution = false });

// scraped_documents container name — try common names.
var containerName = Environment.GetEnvironmentVariable("DOCS_CONTAINER") ?? "scraped_documents";
var docs = client.GetContainer(db, containerName);

// Query plan fails on this box (ServiceInterop) → use ReadMany via cross-partition
// is also a query. Instead, stream with a query and tolerate the failure by paging
// the gateway. If it throws, fall back to a message.
try
{
    var q = new QueryDefinition(
        "SELECT c.id, c.machine_id, c.document_url, c.manufacturer, c.machine_title, c.edition, c.resolution_strategy " +
        "FROM c WHERE CONTAINS(LOWER(c.document_url), 'godzilla')");
    using var it = docs.GetItemQueryIterator<JObject>(q,
        requestOptions: new QueryRequestOptions { MaxConcurrency = 1 });
    int n = 0;
    while (it.HasMoreResults)
    {
        foreach (var d in await it.ReadNextAsync())
        {
            n++;
            string F(string k) { var p = d.Properties().FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase)); return p?.Value?.ToString() ?? ""; }
            var url = F("document_url");
            var file = url.Split('/').LastOrDefault() ?? url;
            Console.WriteLine($"  id={F("id"),-58} -> {F("machine_id"),-14} [{F("manufacturer")}] ed={F("edition")}");
        }
    }
    Console.WriteLine($"  ({n} Godzilla scraped_documents rows)");
}
catch (Exception ex)
{
    Console.WriteLine($"QUERY FAILED ({ex.GetType().Name}): {ex.Message.Split('\n')[0]}");
    Console.WriteLine("(query-plan ServiceInterop bug — fall back to AI Search facet after rebuild)");
}
