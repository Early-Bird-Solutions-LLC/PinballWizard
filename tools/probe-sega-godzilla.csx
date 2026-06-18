#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var m=c.GetContainer("pinwiz","machines");
foreach(var pk in new[]{"sega","stern","gottlieb"}){
  try{ var r=await m.ReadItemAsync<JObject>("G5po2-MeP6B",new PartitionKey(pk));
    Console.WriteLine($"G5po2-MeP6B found in pk={pk}: title='{r.Resource["title"]}' mfr='{r.Resource["manufacturerDisplayName"]}' year={r.Resource["year"]}");
    break;
  }catch(CosmosException){ }
}
