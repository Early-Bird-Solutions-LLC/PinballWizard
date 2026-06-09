#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var raw=c.GetContainer("pinwiz","scraped_documents_raw");
// Point-read the 5 known Godzilla raw docs: do they have a local file already?
foreach(var id in new[]{"doc_b1d3a60ec154d328","doc_58c56c2ec9dfb4df","doc_6e235388cf0e319c","doc_e9ef4f13b3ce1955","doc_7c88f471a0eae8d7"}){
  try{
    var r=await raw.ReadItemAsync<JObject>(id,new PartitionKey(id));
    var localPath=r.Resource.SelectToken("file.localPath")?.ToString();
    var status=r.Resource["linkStatus"]?.ToString() ?? r.Resource.SelectToken("linking.status")?.ToString() ?? "?";
    Console.WriteLine($"{id}: localFile={(string.IsNullOrEmpty(localPath)?"<none>":localPath)}  linkStatus={status}");
  }catch(CosmosException ex){ Console.WriteLine($"{id}: {ex.StatusCode}"); }
}
