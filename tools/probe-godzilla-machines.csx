#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var m=c.GetContainer("pinwiz","machines");
var q=new QueryDefinition("SELECT * FROM c WHERE CONTAINS(LOWER(c.title), 'godzilla')");
var it=m.GetItemQueryIterator<JObject>(q);
while(it.HasMoreResults){
  foreach(var doc in await it.ReadNextAsync()){
    Console.WriteLine(doc.ToString(Newtonsoft.Json.Formatting.Indented));
    Console.WriteLine("---");
  }
}
