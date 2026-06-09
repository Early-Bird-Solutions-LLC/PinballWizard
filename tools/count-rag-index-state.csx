#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
using Microsoft.Azure.Cosmos; using Azure.Identity;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var s=c.GetContainer("pinwiz","rag_index_state");
// Try a simple SELECT * (no projection/aggregate) to confirm emptiness vs query failure.
var it=s.GetItemQueryIterator<System.Text.Json.JsonElement>(new QueryDefinition("SELECT * FROM c"));
int n=0; while(it.HasMoreResults){ foreach(var _ in await it.ReadNextAsync()) n++; }
Console.WriteLine($"rag_index_state row count: {n}");
