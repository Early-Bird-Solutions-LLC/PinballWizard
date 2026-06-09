#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var s=c.GetContainer("pinwiz","rag_index_state");
// Stream all rows (simple SELECT, no aggregate) and point-delete each by id+pk (/document_id).
var it=s.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.id, c.document_id FROM c"));
int deleted=0, failed=0;
while(it.HasMoreResults){
  foreach(var row in await it.ReadNextAsync()){
    var id=row["id"]?.ToString(); var pk=row["document_id"]?.ToString();
    if(string.IsNullOrEmpty(id)||string.IsNullOrEmpty(pk)){ failed++; continue; }
    try{ await s.DeleteItemAsync<JObject>(id,new PartitionKey(pk)); deleted++; }
    catch(CosmosException ex){ Console.WriteLine($"  delete failed {id}: {ex.StatusCode}"); failed++; }
  }
}
Console.WriteLine($"rag_index_state cleared: deleted={deleted} failed={failed}");
