#nullable enable
#r "nuget: Azure.Search.Documents, 11.6.0"
#r "nuget: Azure.Identity, 1.13.1"
using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

var client = new SearchClient(
    new Uri("https://pinwiz-search-dev-buutj.search.windows.net"),
    "pinwiz-rag-v1",
    new DefaultAzureCredential());

foreach (var machineId in new[] { "G5po2-MeP6B", "GweeP-Ml9pZ", "GweeP-MW95j" })
{
    var options = new SearchOptions
    {
        Filter = $"machine_id eq '{machineId}'",
        Size = 0,
        IncludeTotalCount = true,
    };
    var result = await client.SearchAsync<SearchDocument>("*", options);
    Console.WriteLine($"machine_id={machineId}: {result.Value.TotalCount} chunks");
}
