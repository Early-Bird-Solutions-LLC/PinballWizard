#nullable enable
#r "nuget: Azure.Search.Documents, 11.6.0"
#r "nuget: Azure.Identity, 1.13.1"
#r "nuget: Newtonsoft.Json, 13.0.3"
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Identity;
using Newtonsoft.Json.Linq;

var datasetPath = @".\data\eval\findability.v1.jsonl";
var client = new SearchClient(new Uri("https://pinwiz-search-dev-buutj.search.windows.net"),
    "pinwiz-machines-v1", new DefaultAzureCredential());

async Task<List<string>> Search(string query, int top)
{
    var o = new SearchOptions { Size = top, ScoringProfile = "machine-content-intrinsic", QueryType = SearchQueryType.Simple };
    o.SearchFields.Add("title"); o.SearchFields.Add("title_prefix"); o.SearchFields.Add("title_phonetic");
    o.Select.Add("id");
    var ids = new List<string>();
    var r = await client.SearchAsync<SearchDocument>(query, o);
    await foreach (var x in r.Value.GetResultsAsync()) ids.Add(x.Document.GetString("id"));
    return ids;
}

int total = 0, hit1 = 0, hit3 = 0, hit5 = 0; double mrrSum = 0;
var byCat = new Dictionary<string, (int n, int hit3)>();
var misses = new List<string>();

foreach (var line in File.ReadLines(datasetPath))
{
    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
    var o = JObject.Parse(line);
    var query = (string)o["query"]!;
    var cat = (string?)o["category"] ?? "?";
    var expected = new HashSet<string>(o["expected_opdb_ids"]!.Select(x => (string)x!), StringComparer.OrdinalIgnoreCase);
    var ranked = await Search(query, 10);
    total++;

    bool h1 = ranked.Take(1).Any(expected.Contains);
    bool h3 = ranked.Take(3).Any(expected.Contains);
    bool h5 = ranked.Take(5).Any(expected.Contains);
    if (h1) hit1++; if (h3) hit3++; if (h5) hit5++;
    int rank = ranked.FindIndex(expected.Contains);
    if (rank >= 0) mrrSum += 1.0 / (rank + 1);
    if (!h5) misses.Add($"{cat}/\"{query}\"");

    var c = byCat.GetValueOrDefault(cat); byCat[cat] = (c.n + 1, c.hit3 + (h3 ? 1 : 0));
}

Console.WriteLine($"=== Findability retrieval vs live machine index ({total} probes) ===");
Console.WriteLine($"Hit@1  : {hit1}/{total} = {100.0*hit1/total:F0}%");
Console.WriteLine($"Hit@3  : {hit3}/{total} = {100.0*hit3/total:F0}%");
Console.WriteLine($"Hit@5  : {hit5}/{total} = {100.0*hit5/total:F0}%");
Console.WriteLine($"MRR    : {mrrSum/total:F3}");
Console.WriteLine($"\n--- Hit@3 by category ---");
foreach (var kv in byCat.OrderBy(k => k.Key))
    Console.WriteLine($"  {kv.Key,-20} {kv.Value.hit3}/{kv.Value.n}");
Console.WriteLine($"\n--- misses (not in top-5) ---");
foreach (var m in misses) Console.WriteLine($"  {m}");
if (misses.Count == 0) Console.WriteLine("  (none)");
Console.WriteLine("\nDONE.");
