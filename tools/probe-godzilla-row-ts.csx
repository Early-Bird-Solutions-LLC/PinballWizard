#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var d=c.GetContainer("pinwiz","scraped_documents");
// The two fan-out rows for the Pro manual: compare _ts (Cosmos write time).
foreach(var (id,pk) in new[]{("doc_b1d3a60ec154d328_GweeP-MW95j","GweeP-MW95j"),("doc_b1d3a60ec154d328_GweeP-Ml9pZ","GweeP-Ml9pZ")}){
  try{
    var r=await d.ReadItemAsync<JObject>(id,new PartitionKey(pk));
    var ts=r.Resource["_ts"]?.Value<long>()??0;
    var when=DateTimeOffset.FromUnixTimeSeconds(ts).ToString("u");
    Console.WriteLine($"{id}");
    Console.WriteLine($"  _ts={ts} ({when})  edition_scope={r.Resource["edition_scope"]}  edition={r.Resource["edition"]}");
  }catch(CosmosException ex){ Console.WriteLine($"{id}: {ex.StatusCode}"); }
}
Console.WriteLine($"\nNow (UTC): {DateTimeOffset.UtcNow:u}");
