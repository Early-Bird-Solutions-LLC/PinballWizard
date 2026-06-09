#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var raw=c.GetContainer("pinwiz","scraped_documents_raw");
foreach(var id in new[]{"doc_b1d3a60ec154d328","doc_58c56c2ec9dfb4df","doc_6e235388cf0e319c","doc_536d898871ecdfd0","doc_b4d0a3bf9f5052d0"}){
  try{
    var r=await raw.ReadItemAsync<JObject>(id,new PartitionKey(id));
    var o=r.Resource;
    Console.WriteLine($"{id}:");
    Console.WriteLine($"  filename       = {System.IO.Path.GetFileName(o.SelectToken("source.file_url")?.ToString()??"")}");
    Console.WriteLine($"  link_status    = {o["link_status"]}");
    Console.WriteLine($"  resolution     = {o["resolution_strategy"]}");
    Console.WriteLine($"  linked_ids     = {o["linked_machine_ids"]?.ToString(Newtonsoft.Json.Formatting.None)}");
    Console.WriteLine($"  local_path     = {o.SelectToken("file.local_path")}");
  }catch(CosmosException ex){ Console.WriteLine($"{id}: {ex.StatusCode}"); }
}
