#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Microsoft.Azure.Cosmos; using Azure.Identity; using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
var c=new CosmosClient("https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/",new DefaultAzureCredential(),new CosmosClientOptions{ConnectionMode=ConnectionMode.Gateway});
var raw=c.GetContainer("pinwiz","scraped_documents_raw");
// Verify the 5 Godzilla docs (representative; covers manualspage + gamepage source types).
var ids=new[]{"doc_b1d3a60ec154d328","doc_58c56c2ec9dfb4df","doc_6e235388cf0e319c","doc_e9ef4f13b3ce1955","doc_7c88f471a0eae8d7"};
int ok=0, mismatch=0, missing=0;
foreach(var id in ids){
  var r=await raw.ReadItemAsync<JObject>(id,new PartitionKey(id));
  var lp=r.Resource.SelectToken("file.local_path")?.ToString();
  var recorded=r.Resource.SelectToken("file.sha256")?.ToString();
  if(string.IsNullOrEmpty(lp)){ Console.WriteLine($"{id}: no local_path"); missing++; continue; }
  // Linker resolves Path.Combine(downloadsRoot='data/downloads', local_path) -> the doubled on-disk path.
  var onDisk=System.IO.Path.Combine("data/downloads", lp);
  if(!System.IO.File.Exists(onDisk)){ Console.WriteLine($"{id}: FILE MISSING at {onDisk}"); missing++; continue; }
  using var fs=System.IO.File.OpenRead(onDisk);
  var actual=Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
  var match=string.Equals(actual,recorded,StringComparison.OrdinalIgnoreCase);
  Console.WriteLine($"{id}: {(match?"OK":"MISMATCH")}  recorded={recorded?.Substring(0,12)}.. actual={actual.Substring(0,12)}..  ({System.IO.Path.GetFileName(lp)})");
  if(match) ok++; else mismatch++;
}
Console.WriteLine($"\nSUMMARY: {ok} OK / {mismatch} mismatch / {missing} missing (of {ids.Length})");
