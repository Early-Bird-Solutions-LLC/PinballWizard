#!/usr/bin/env dotnet-script
// Read-only confidence probe for the AB#259 edition-aware reconcile (BEFORE the
// 30-min --source all). Answers, against LIVE Cosmos `machines`:
//   1. Are GroupId + Year actually populated? (the edition-family rule needs both)
//   2. Simulate the new edition-family rule (same partition + same GroupId + same
//      Year, franchise-title collision) — how many families, how many machines
//      would get a slug, and does it OVER-fire across the messy real catalog?
//   3. Show the Godzilla rows + a sample of detected families for eyeball review.
// No writes. Usage: dotnet script tools/probe-edition-family-dryrun.csx

#nullable enable
#r "nuget: Microsoft.Azure.Cosmos, 3.46.0"
#r "nuget: Azure.Identity, 1.13.1"

using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Azure.Identity;

var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/";
var dbName = Environment.GetEnvironmentVariable("COSMOS_DB") ?? "pinwiz";

var client = new CosmosClient(endpoint, new DefaultAzureCredential(),
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
var machines = client.GetContainer(dbName, "machines");

// Mirror the production NormalizeFranchiseTitle: strip trailing "(edition)" then
// lowercase + strip non-alphanumeric + trailing decoration words.
static readonly string[] Decorations =
    { "remake", "pinball", "gamekit", "deposit", "limitededition", "merlinedition", "vaultedition", "standardedition", "edition" };
static string NormFranchise(string title)
{
    if (string.IsNullOrWhiteSpace(title)) return "";
    var t = title.TrimEnd();
    var open = t.LastIndexOf('(');
    if (open > 0 && t.EndsWith(")")) t = t[..open];
    var sb = new System.Text.StringBuilder();
    foreach (var c in t) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
    var n = sb.ToString();
    foreach (var d in Decorations)
        if (n.Length > d.Length && n.EndsWith(d)) { n = n[..^d.Length]; break; }
    return n;
}

// Pull just the fields the rule needs.
var rows = new List<(string Id, string Pk, string Title, string? Group, int? Year)>();
using (var it = machines.GetItemQueryIterator<dynamic>(new QueryDefinition(
    "SELECT c.id, c.manufacturer, c.title, c.groupId, c.year FROM c")))
{
    while (it.HasMoreResults)
        foreach (var r in await it.ReadNextAsync())
        {
            rows.Add((
                (string)r.id,
                (string)(r.manufacturer ?? "<null>"),
                (string)(r.title ?? ""),
                r.groupId == null ? null : (string)r.groupId,
                r.year == null ? (int?)null : (int)r.year));
        }
}

Console.WriteLine($"machines total: {rows.Count}");

// Q1: field population.
int withGroup = rows.Count(r => !string.IsNullOrEmpty(r.Group));
int withYear  = rows.Count(r => r.Year is not null);
int withBoth  = rows.Count(r => !string.IsNullOrEmpty(r.Group) && r.Year is not null);
Console.WriteLine($"  with GroupId:        {withGroup}  ({100.0*withGroup/rows.Count:F0}%)");
Console.WriteLine($"  with Year:           {withYear}  ({100.0*withYear/rows.Count:F0}%)");
Console.WriteLine($"  with BOTH (rule-eligible): {withBoth}  ({100.0*withBoth/rows.Count:F0}%)");

// Q2: simulate the edition-family rule. Group rule-eligible machines by
// (partition, groupId, year, franchiseTitle). A group with >1 member is an
// edition family that WOULD get the slug written to all members.
var families = rows
    .Where(r => !string.IsNullOrEmpty(r.Group) && r.Year is not null)
    .GroupBy(r => (r.Pk, r.Group, r.Year, Franchise: NormFranchise(r.Title)))
    .Where(g => g.Count() > 1)
    .ToList();

int machinesInFamilies = families.Sum(g => g.Count());
Console.WriteLine($"--- simulated edition families (same partition+group+year, franchise collision) ---");
Console.WriteLine($"  families detected:           {families.Count}");
Console.WriteLine($"  machines in families:        {machinesInFamilies}");
Console.WriteLine($"  avg family size:             {(families.Count == 0 ? 0 : (double)machinesInFamilies/families.Count):F1}");

// Over-fire guard: families whose members DON'T all share the franchise title
// would be a bug. By construction they do (franchise is part of the key), but
// double-check that no family mixes different real franchises via the parenthetical.
var suspicious = families.Where(g => g.Select(m => NormFranchise(m.Title)).Distinct().Count() > 1).ToList();
Console.WriteLine($"  families with MIXED franchise (should be 0): {suspicious.Count}");

// Largest families — eyeball for over-merge.
Console.WriteLine("--- 10 largest detected families ---");
foreach (var g in families.OrderByDescending(x => x.Count()).Take(10))
    Console.WriteLine($"  [{g.Key.Pk}] group={g.Key.Group} year={g.Key.Year} '{g.Key.Franchise}': {string.Join(", ", g.Select(m => m.Title + "=" + m.Id))}");

// Q3: Godzilla specifically — does the rule pick up Pro + Premium/LE as ONE family?
Console.WriteLine("--- Godzilla machines (live) ---");
foreach (var r in rows.Where(r => r.Title.ToLowerInvariant().Contains("godzilla")).OrderBy(r => r.Id))
    Console.WriteLine($"  {r.Id,-18} pk={r.Pk,-8} group={r.Group ?? "null",-8} year={r.Year?.ToString() ?? "null",-6} '{r.Title}'  franchise='{NormFranchise(r.Title)}'");
