#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var lu=c.GetContainer("pinwiz","machine_title_lookups");
// Count total rows and find most-recently updated
var q=lu.GetItemQueryIterator<JObject>("SELECT COUNT(1) AS cnt FROM c");
var countPage=await q.ReadNextAsync();
Console.WriteLine($"Total rows: {countPage.FirstOrDefault()?["cnt"]}");


// Find last 5 updated rows
var recent=lu.GetItemQueryIterator<JObject>("SELECT TOP 5 c.id,c._ts FROM c ORDER BY c._ts DESC");
while(recent.HasMoreResults){
  var page=await recent.ReadNextAsync();
  foreach(var row in page){
    var ts=DateTimeOffset.FromUnixTimeSeconds(row["_ts"]?.Value<long>()??0);
    Console.WriteLine($"  {row["id"],-40} {ts:HH:mm:ss}");
  }
}
