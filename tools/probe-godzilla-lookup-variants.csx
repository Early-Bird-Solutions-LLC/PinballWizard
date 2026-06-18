#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var lu=c.GetContainer("pinwiz","machine_title_lookups");
foreach(var key in new[]{"godzilla","stern godzilla","godzilla premium","godzilla pro","godzilla le","godzilla premium/le","stern godzilla premium","godzilla (premium)"}){
  try{
    var r=await lu.ReadItemAsync<JObject>(key,new PartitionKey(key));
    var o=r.Resource;
    Console.WriteLine($"[{key}]: opdbIds={o["opdbIds"]?.ToString(Newtonsoft.Json.Formatting.None)} matchTokens={o["matchTokens"]?.ToString(Newtonsoft.Json.Formatting.None)}");
  }catch(CosmosException ex) when((int)ex.StatusCode==404){
    Console.WriteLine($"[{key}]: NOT FOUND");
  }
}
