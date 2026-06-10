#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var lu=c.GetContainer("pinwiz","machine_title_lookups");
var r=await lu.ReadItemAsync<JObject>("stern godzilla",new PartitionKey("stern godzilla"));
Console.WriteLine(r.Resource.ToString(Newtonsoft.Json.Formatting.Indented));
