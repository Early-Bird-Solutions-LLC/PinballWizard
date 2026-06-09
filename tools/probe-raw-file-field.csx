#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var raw=c.GetContainer("pinwiz","scraped_documents_raw");
var r=await raw.ReadItemAsync<JObject>("doc_b1d3a60ec154d328",new PartitionKey("doc_b1d3a60ec154d328"));
// Dump the top-level keys + the 'file' and link-status fields verbatim
Console.WriteLine("top-level keys: "+string.Join(", ", r.Resource.Properties().Select(p=>p.Name)));
Console.WriteLine("file = "+(r.Resource["file"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "<null>"));
foreach(var k in new[]{"linkStatus","link_status","status","linking"})
  if(r.Resource[k]!=null) Console.WriteLine($"{k} = {r.Resource[k]!.ToString(Newtonsoft.Json.Formatting.None)}");
