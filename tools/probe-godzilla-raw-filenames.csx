// Read-only: point-read the RAW Godzilla docs (scraped_documents_raw) to show the
// file_url / filename + link_text — the actual input EditionResolver matches on in
// Step 3 re-link. Point-read by the known base doc ids (deterministic SHA ids seen
// in scraped_documents). Partition key on scraped_documents_raw is /document_id.
#nullable enable
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
var raw = c.GetContainer("pinwiz", "scraped_documents_raw");

// Base doc ids observed in scraped_documents (strip the _<machineId> fan-out suffix).
var ids = new[]
{
    "doc_b1d3a60ec154d328",
    "doc_58c56c2ec9dfb4df",
    "doc_6e235388cf0e319c",
    "doc_e9ef4f13b3ce1955",
    "doc_7c88f471a0eae8d7",
};

foreach (var id in ids)
{
    try
    {
        var r = await raw.ReadItemAsync<JObject>(id, new PartitionKey(id));
        var o = r.Resource;
        string Path(params string[] path)
        {
            JToken? cur = o;
            foreach (var seg in path)
            {
                cur = cur?.Children<JProperty>().FirstOrDefault(p => p.Name.Equals(seg, StringComparison.OrdinalIgnoreCase))?.Value;
                if (cur is null) return "<absent>";
            }
            return cur?.ToString()?.Replace("\r", "").Replace("\n", " ") ?? "<null>";
        }
        var fileUrl = Path("source", "file_url");
        var fname = fileUrl != "<absent>" && Uri.TryCreate(fileUrl, UriKind.Absolute, out var u)
            ? System.IO.Path.GetFileName(u.AbsolutePath) : "<n/a>";
        Console.WriteLine($"{id}");
        Console.WriteLine($"    filename  = {fname}");
        Console.WriteLine($"    link_text = {Path("source", "link_text")}");
        Console.WriteLine($"    file_url  = {fileUrl}");
    }
    catch (CosmosException ex)
    {
        Console.WriteLine($"{id}: READ FAILED {ex.StatusCode}");
    }
}
