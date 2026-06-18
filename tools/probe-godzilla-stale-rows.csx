#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var d=c.GetContainer("pinwiz","scraped_documents");
// Point-read all 13 known Godzilla rows by id+pk (from the docs probe), show _ts + edition_scope.
var rows=new (string id,string pk)[]{
 ("doc_b1d3a60ec154d328_GweeP-MW95j","GweeP-MW95j"),("doc_b1d3a60ec154d328_GweeP-Ml9pZ","GweeP-Ml9pZ"),
 ("doc_58c56c2ec9dfb4df_GweeP-MW95j","GweeP-MW95j"),("doc_58c56c2ec9dfb4df_GweeP-Ml9pZ","GweeP-Ml9pZ"),
 ("doc_6e235388cf0e319c_GweeP-MW95j","GweeP-MW95j"),("doc_6e235388cf0e319c_GweeP-Ml9pZ","GweeP-Ml9pZ"),
 ("doc_e9ef4f13b3ce1955_GweeP-MW95j","GweeP-MW95j"),("doc_e9ef4f13b3ce1955_GweeP-Ml9pZ","GweeP-Ml9pZ"),
 ("doc_7c88f471a0eae8d7_GweeP-MW95j","GweeP-MW95j"),("doc_7c88f471a0eae8d7_GweeP-Ml9pZ","GweeP-Ml9pZ"),
 ("doc_536d898871ecdfd0_GweeP-MW95j","GweeP-MW95j"),
 ("doc_b4d0a3bf9f5052d0_GweeP-Ml9pZ","GweeP-Ml9pZ"),
 ("doc_c4e5cbac5fab9f4c_GweeP-Ml9pZ","GweeP-Ml9pZ"),
};
long cutoff=1780500000; // ~2026-06-03 15:20Z — anything older than today's relink is pre-existing
foreach(var (id,pk) in rows){
  try{
    var r=await d.ReadItemAsync<JObject>(id,new PartitionKey(pk));
    var ts=r.Resource["_ts"]?.Value<long>()??0;
    var scope=r.Resource["edition_scope"]?.ToString()??"";
    var fn=System.IO.Path.GetFileName(r.Resource["document_url"]?.ToString()??"");
    var flag=string.IsNullOrEmpty(scope)?"  <-- STALE (empty edition_scope)":"";
    Console.WriteLine($"{(ts<cutoff?"OLD":"NEW")} _ts={ts} scope='{scope}' {id}{flag}");
  }catch(CosmosException){ Console.WriteLine($"GONE {id}"); }
}
