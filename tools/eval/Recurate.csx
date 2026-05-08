// tools/eval/Recurate.csx
// ---------------------------------------------------------------------------
// One-off recuration tool for `data/eval/wizard.v1.jsonl`.
//
// WHY THIS EXISTS
//   Phase 3 PR 8 (eval harness) shipped wizard.v1.jsonl with subagent-curated
//   plausible OPDB-format ids ("GRBN-MQR4P" etc.). The deployed Cosmos catalog
//   contains the *actual* OPDB ids. They don't match. When the agent calls
//   getMachineByTitle("Godzilla") and gets back the catalog record, it cites
//   the real id — but expected_citation_set holds a different id, so
//   citation_precision and citation_recall score 0 even on a correct lookup.
//   This is one of the two reasons H2 baseline citation_precision was 0.133.
//   See build-spec.md § Phase 4 § Scope item 9 and the Phase 3 retrospective
//   lesson 5 for the spec.
//
// WHAT IT DOES
//   For each row in data/eval/wizard.v1.jsonl:
//     1. Looks up the question's curated machine title AND the curated
//        expected_manufacturer in tools/eval/wizard.v1.titles.json
//        (the side-car).
//     2. If the title is null (out-of-scope rows; acceptable_refusal=true)
//        the row's expected_citation_set is left untouched and the row is
//        recorded as "skipped (out-of-scope)" in the summary.
//     3. Otherwise queries the deployed Cosmos `machines` container via
//        IMachineRepository.QueryByTitleAsync semantics — i.e. case-
//        insensitive STRINGEQUALS on c.title.
//     4. If expected_manufacturer is non-null, walks the result set and
//        picks the first hit whose `manufacturer` matches (case-
//        insensitive). If no hit matches, the row is skipped with status
//        `mfg_mismatch` and the JSONL is left untouched — this is the
//        2026-05-08 hardening that catches the silent-mis-match failure
//        mode where a title is shared across manufacturers (e.g.
//        Stern's 2021 Godzilla vs. Sega's 1998 Godzilla).
//     5. If expected_manufacturer is null on an in-scope row, falls back
//        to first-hit-wins and logs a "manufacturer-unconstrained"
//        warning so a future audit can tighten the side-car.
//     6. Replaces the row's expected_citation_set with [actualOpdbId].
//
// OUTPUTS
//   - data/eval/wizard.v1.jsonl              : updated in place (preserves
//                                               curator comment lines)
//   - data/eval/wizard.v1.recuration.json    : provenance side-car —
//                                               recuration timestamp UTC,
//                                               Cosmos endpoint, jsonl SHA
//                                               before recuration, script
//                                               git SHA, per-question outcome.
//
// USAGE
//   1. `az login` to the personal Earlybird subscription.
//   2. From the repo root:
//        dotnet script tools/eval/Recurate.csx -- --dry-run
//      (always run --dry-run first; the script prints proposed changes
//      without writing anything.)
//   3. After verifying the dry-run output:
//        dotnet script tools/eval/Recurate.csx
//      The Cosmos endpoint defaults to the personal-dev account; override
//      with --cosmos-endpoint <url> or PINWIZ_COSMOS_ENDPOINT env var.
//
// FLAGS
//   --dry-run                Print proposed changes only; don't write the
//                            jsonl or recuration side-car.
//   --cosmos-endpoint <url>  Override the deployed Cosmos endpoint URL.
//                            Falls back to $PINWIZ_COSMOS_ENDPOINT, then
//                            to the personal-dev default below.
//   --jsonl <path>           Override the path to wizard.v1.jsonl.
//   --titles <path>          Override the path to wizard.v1.titles.json.
//
// NOT A PRODUCTION CODE PATH
//   This script does not compile as part of PinballWizard.slnx. It is a
//   dotnet-script .csx file invoked manually. The query semantics
//   (case-insensitive STRINGEQUALS on c.title) intentionally mirror
//   IMachineRepository.QueryByTitleAsync in
//   src/PinballWizard.Infrastructure/Persistence/Cosmos/MachineRepository.cs
//   so a recurated id reflects what the production getMachineByTitle
//   function tool would actually return.
// ---------------------------------------------------------------------------

// NOTE: Microsoft.Azure.Cosmos pinned at 3.43.1 (one minor below the
// production csproj's 3.59.0). SDK 3.59.0's query path returns
// "BadRequest: One of the specified inputs is invalid" against
// serverless accounts on .NET 10 — both partition-keyed and
// cross-partition queries. 3.43.1 is the highest version where the
// query path round-trips cleanly under dotnet-script on .NET 10. Pin
// stays here until Microsoft.Azure.Cosmos ships a fix; production
// `MachineRepository.QueryByTitleAsync` is unaffected because it runs
// inside the .NET 10 host project, not under dotnet-script.
#r "nuget: Microsoft.Azure.Cosmos, 3.43.1"
#r "nuget: Azure.Identity, 1.21.0"

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Azure.Identity;

// ---- Configuration defaults ------------------------------------------------

const string DefaultCosmosEndpoint = "https://pinwiz-cosmos-dev-hlpz4.documents.azure.com:443/";
const string CosmosDatabase = "pinwiz";
const string CosmosContainer = "machines";
const string CosmosEndpointEnvVar = "PINWIZ_COSMOS_ENDPOINT";

// ---- Resolve script location → repo paths ----------------------------------
// dotnet-script does not expose a `__SOURCE_DIRECTORY__`-style global; we
// derive the repo root from the script's invocation path. The script lives
// at tools/eval/Recurate.csx so the repo root is two levels up from the
// script file. CallerFilePath is computed at compile time and is stable.

string ScriptDir() {
    var loc = GetScriptFilePath();
    return Path.GetDirectoryName(loc) ?? Directory.GetCurrentDirectory();
}

string GetScriptFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

string repoRoot = Path.GetFullPath(Path.Combine(ScriptDir(), "..", ".."));
string defaultJsonlPath = Path.Combine(repoRoot, "data", "eval", "wizard.v1.jsonl");
string defaultTitlesPath = Path.Combine(repoRoot, "tools", "eval", "wizard.v1.titles.json");
string defaultRecurationPath = Path.Combine(repoRoot, "data", "eval", "wizard.v1.recuration.json");

// ---- Parse args ------------------------------------------------------------

var argv = Args.ToList();
bool dryRun = argv.Remove("--dry-run");
string cosmosEndpoint = ExtractValueArg(argv, "--cosmos-endpoint")
    ?? Environment.GetEnvironmentVariable(CosmosEndpointEnvVar)
    ?? DefaultCosmosEndpoint;
string jsonlPath = ExtractValueArg(argv, "--jsonl") ?? defaultJsonlPath;
string titlesPath = ExtractValueArg(argv, "--titles") ?? defaultTitlesPath;
string recurationPath = defaultRecurationPath;

if (argv.Count > 0)
{
    Console.Error.WriteLine($"Unrecognized arguments: {string.Join(" ", argv)}");
    Console.Error.WriteLine("Usage: dotnet script tools/eval/Recurate.csx -- [--dry-run] [--cosmos-endpoint <url>] [--jsonl <path>] [--titles <path>]");
    Environment.Exit(2);
}

string? ExtractValueArg(List<string> args, string flag)
{
    var idx = args.IndexOf(flag);
    if (idx < 0) return null;
    if (idx + 1 >= args.Count) {
        Console.Error.WriteLine($"Flag {flag} requires a value.");
        Environment.Exit(2);
    }
    var value = args[idx + 1];
    args.RemoveAt(idx + 1);
    args.RemoveAt(idx);
    return value;
}

// ---- Load the inputs -------------------------------------------------------

if (!File.Exists(jsonlPath))
{
    Console.Error.WriteLine($"FATAL: ground-truth file not found at '{jsonlPath}'.");
    Environment.Exit(2);
}
if (!File.Exists(titlesPath))
{
    Console.Error.WriteLine($"FATAL: titles side-car not found at '{titlesPath}'.");
    Console.Error.WriteLine("This file maps each question id to its curated machine title.");
    Environment.Exit(2);
}

string jsonlContentBefore = File.ReadAllText(jsonlPath);
string jsonlSha = ShortSha256(jsonlContentBefore);

var titlesDoc = JsonDocument.Parse(File.ReadAllText(titlesPath));
var titleMap = new Dictionary<string, TitleSidecarEntry>(StringComparer.Ordinal);
foreach (var entry in titlesDoc.RootElement.GetProperty("questions").EnumerateArray())
{
    string id = entry.GetProperty("id").GetString()!;
    string? title = entry.TryGetProperty("machine_title", out var tEl) && tEl.ValueKind == JsonValueKind.String
        ? tEl.GetString()
        : null;
    string? expectedMfg = entry.TryGetProperty("expected_manufacturer", out var mEl) && mEl.ValueKind == JsonValueKind.String
        ? mEl.GetString()
        : null;
    titleMap[id] = new TitleSidecarEntry(title, expectedMfg);
}

Console.WriteLine($"Recuration script v1 — {(dryRun ? "DRY RUN" : "LIVE")}");
Console.WriteLine($"  jsonl path:        {jsonlPath}");
Console.WriteLine($"  titles path:       {titlesPath}");
Console.WriteLine($"  cosmos endpoint:   {cosmosEndpoint}");
Console.WriteLine($"  jsonl SHA-256/16:  {jsonlSha}");
Console.WriteLine($"  title map size:    {titleMap.Count} questions");
Console.WriteLine();

// ---- Connect to Cosmos -----------------------------------------------------

CosmosClientOptions clientOptions = new()
{
    ApplicationName = "pinwiz-eval-recurate",
};

// DefaultAzureCredential matches the production wiring
// (src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs)
// so the script's auth path mirrors what FoundryAgentFactory + the
// CosmosBootstrapper actually use against deployed Cosmos.
CosmosClient cosmos;
try
{
    cosmos = new CosmosClient(cosmosEndpoint, new DefaultAzureCredential(), clientOptions);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: failed to construct CosmosClient: {ex.Message}");
    Console.Error.WriteLine("Hint: ensure `az login` is active on the personal Earlybird subscription.");
    Environment.Exit(2);
    throw;
}

Container container = cosmos.GetContainer(CosmosDatabase, CosmosContainer);

// Probe the container with a tiny query so an auth failure surfaces
// here (with a clear error) rather than mid-iteration.
try
{
    using var probeIter = container.GetItemQueryIterator<TitleHit>(
        new QueryDefinition("SELECT TOP 1 c.id FROM c"));
    _ = await probeIter.ReadNextAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: Cosmos probe query failed: {ex.Message}");
    Console.Error.WriteLine("Hint: verify the Cosmos endpoint URL and that your account has data-plane read access.");
    Environment.Exit(2);
    throw;
}

Console.WriteLine("Cosmos connection probe: OK");
Console.WriteLine();

// ---- Process each row ------------------------------------------------------

var lines = File.ReadAllLines(jsonlPath);
var rewritten = new List<string>(lines.Length);
var outcomes = new List<RecurationOutcome>();
int processed = 0, recurated = 0, unchanged = 0, skippedOos = 0, skippedNoMatch = 0, skippedMfgMismatch = 0, mfgUnconstrained = 0;
var stopwatch = Stopwatch.StartNew();

// UnsafeRelaxedJsonEscaping preserves apostrophes / ampersands / em-dashes
// as-is in the rewritten JSONL; the default HTML-safe encoder would
// expand "Stern's" to "Stern's" which is technically equivalent
// JSON but obfuscates a hand-curated file that humans need to read.
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

for (int i = 0; i < lines.Length; i++)
{
    string raw = lines[i];
    string trimmed = raw.TrimStart();

    if (string.IsNullOrWhiteSpace(raw) || trimmed.StartsWith('#'))
    {
        rewritten.Add(raw);
        continue;
    }

    EvalQuestion? row;
    try
    {
        row = JsonSerializer.Deserialize<EvalQuestion>(trimmed, jsonOptions);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"FATAL: line {i + 1} parse error: {ex.Message}");
        Environment.Exit(2);
        throw;
    }
    if (row is null)
    {
        Console.Error.WriteLine($"FATAL: line {i + 1} parsed to null.");
        Environment.Exit(2);
        throw new UnreachableException();
    }

    processed++;

    if (!titleMap.TryGetValue(row.Id, out var sidecar))
    {
        Console.WriteLine($"  [{row.Id}] no entry in titles side-car — leaving unchanged");
        outcomes.Add(new RecurationOutcome(row.Id, null, null, null, null, "missing_in_titles_sidecar"));
        rewritten.Add(raw);
        unchanged++;
        continue;
    }

    string? curatedTitle = sidecar.MachineTitle;
    string? expectedMfg = sidecar.ExpectedManufacturer;

    if (curatedTitle is null)
    {
        Console.WriteLine($"  [{row.Id}] out-of-scope (machine_title=null) — leaving expected_citation_set as-is ([{string.Join(",", row.ExpectedCitationSet)}])");
        outcomes.Add(new RecurationOutcome(row.Id, null, null, null, null, "out_of_scope"));
        rewritten.Add(raw);
        skippedOos++;
        continue;
    }

    var hits = await QueryHitsByTitle(container, curatedTitle, default);

    if (hits.Count == 0)
    {
        Console.WriteLine($"  [{row.Id}] title='{curatedTitle}' NOT FOUND in deployed Cosmos — leaving unchanged");
        outcomes.Add(new RecurationOutcome(row.Id, curatedTitle, expectedMfg, null, null, "no_match"));
        rewritten.Add(raw);
        skippedNoMatch++;
        continue;
    }

    string? actualOpdbId;
    string? resolvedManufacturer;

    if (expectedMfg is null)
    {
        // Manufacturer-unconstrained — fall back to first-hit-wins and
        // log a warning so a future audit can tighten the side-car.
        var firstHit = hits[0];
        actualOpdbId = firstHit.Id;
        resolvedManufacturer = firstHit.Manufacturer;
        mfgUnconstrained++;
        Console.WriteLine($"  [{row.Id}] WARNING: manufacturer-unconstrained (expected_manufacturer is null in side-car) — first-hit-wins picked '{actualOpdbId}' (mfg={resolvedManufacturer}); consider adding expected_manufacturer to wizard.v1.titles.json");
    }
    else
    {
        var match = hits.FirstOrDefault(h =>
            string.Equals(h.Manufacturer, expectedMfg, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            string returnedMfgs = string.Join(", ",
                hits.Select(h => h.Manufacturer ?? "<null>").Distinct(StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"  [{row.Id}] MFG_MISMATCH: title='{curatedTitle}' returned {hits.Count} hit(s) under mfg(s) [{returnedMfgs}] but expected_manufacturer='{expectedMfg}' — leaving unchanged");
            outcomes.Add(new RecurationOutcome(row.Id, curatedTitle, expectedMfg, null, null, "mfg_mismatch"));
            rewritten.Add(raw);
            skippedMfgMismatch++;
            continue;
        }
        actualOpdbId = match.Id;
        resolvedManufacturer = match.Manufacturer;
    }

    string oldCitations = "[" + string.Join(",", row.ExpectedCitationSet) + "]";
    string newCitations = $"[{actualOpdbId}]";
    string verdict = oldCitations == newCitations ? "(unchanged)" : "(updated)";
    Console.WriteLine($"  [{row.Id}] title='{curatedTitle}' → {actualOpdbId} (mfg={resolvedManufacturer}) {verdict}");

    outcomes.Add(new RecurationOutcome(row.Id, curatedTitle, expectedMfg, resolvedManufacturer, actualOpdbId,
        oldCitations == newCitations ? "unchanged" : "recurated"));

    if (oldCitations != newCitations)
    {
        recurated++;
        var updatedRow = row with { ExpectedCitationSet = new[] { actualOpdbId } };
        rewritten.Add(JsonSerializer.Serialize(updatedRow, jsonOptions));
    }
    else
    {
        unchanged++;
        rewritten.Add(raw);
    }
}

stopwatch.Stop();

// ---- Summary ---------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Summary");
Console.WriteLine($"  Questions processed:           {processed}");
Console.WriteLine($"  Recurated (id changed):        {recurated}");
Console.WriteLine($"  Unchanged (id matched):        {unchanged}");
Console.WriteLine($"  Skipped (out-of-scope):        {skippedOos}");
Console.WriteLine($"  Skipped (no Cosmos hit):       {skippedNoMatch}");
Console.WriteLine($"  Skipped (mfg mismatch):        {skippedMfgMismatch}");
Console.WriteLine($"  Manufacturer-unconstrained:    {mfgUnconstrained}");
Console.WriteLine($"  Elapsed:                       {stopwatch.Elapsed.TotalSeconds:F1}s");
Console.WriteLine();

if (skippedNoMatch > 0)
{
    Console.WriteLine("WARNING: one or more curated titles did not resolve in the deployed Cosmos catalog.");
    Console.WriteLine("Investigate before treating this as a clean baseline:");
    Console.WriteLine("  - is the title in tools/eval/wizard.v1.titles.json the canonical OPDB title?");
    Console.WriteLine("  - is the deployed catalog populated (run --source opdb if missing)?");
    Console.WriteLine();
}

if (skippedMfgMismatch > 0)
{
    Console.WriteLine("WARNING: one or more curated titles returned hits under a manufacturer that did not match expected_manufacturer.");
    Console.WriteLine("These rows were left unchanged (no_match-equivalent). Investigate before treating this as a clean baseline:");
    Console.WriteLine("  - is the deployed catalog missing the expected manufacturer's record (e.g. Stern's modern catalog absent)?");
    Console.WriteLine("  - or is expected_manufacturer in tools/eval/wizard.v1.titles.json wrong for this question?");
    Console.WriteLine();
}

if (mfgUnconstrained > 0)
{
    Console.WriteLine("NOTE: one or more in-scope rows have expected_manufacturer=null in the side-car and fell back to first-hit-wins.");
    Console.WriteLine("Tighten wizard.v1.titles.json by adding the expected manufacturer for each warned row.");
    Console.WriteLine();
}

// ---- Write outputs ---------------------------------------------------------

if (dryRun)
{
    Console.WriteLine("DRY RUN — no files written. Re-run without --dry-run to apply.");
    return;
}

File.WriteAllText(jsonlPath, string.Join(Environment.NewLine, rewritten) + Environment.NewLine, new UTF8Encoding(false));
Console.WriteLine($"Wrote: {jsonlPath}");

string scriptSha = ShortSha256(File.ReadAllText(GetScriptFilePath()));
var recuration = new RecurationManifest(
    RecuratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
    CosmosEndpoint: cosmosEndpoint,
    CosmosDatabase: CosmosDatabase,
    CosmosContainer: CosmosContainer,
    JsonlSha256Before: jsonlSha,
    ScriptSha256: scriptSha,
    Counts: new RecurationCounts(processed, recurated, unchanged, skippedOos, skippedNoMatch, skippedMfgMismatch, mfgUnconstrained),
    Outcomes: outcomes
);

File.WriteAllText(recurationPath,
    JsonSerializer.Serialize(recuration, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }) + Environment.NewLine,
    new UTF8Encoding(false));
Console.WriteLine($"Wrote: {recurationPath}");

// ---- Helpers ---------------------------------------------------------------

static async Task<List<TitleHitWithManufacturer>> QueryHitsByTitle(Container container, string title, CancellationToken ct)
{
    // Mirrors IMachineRepository.QueryByTitleAsync — case-insensitive
    // STRINGEQUALS on c.title, cross-partition. Returns all hits for
    // the title so the caller can pick the one that matches the
    // expected manufacturer; titles like "Godzilla" exist under
    // multiple manufacturer partitions (Stern 2021 vs Sega 1998) and
    // the W1-3 hardening (2026-05-08) requires walking the full result
    // set rather than blindly taking the first hit.
    var query = new QueryDefinition("SELECT c.id, c.manufacturer FROM c WHERE STRINGEQUALS(c.title, @title, true)")
        .WithParameter("@title", title);
    var results = new List<TitleHitWithManufacturer>();
    using var iter = container.GetItemQueryIterator<TitleHitWithManufacturer>(query);
    while (iter.HasMoreResults)
    {
        var page = await iter.ReadNextAsync(ct);
        results.AddRange(page);
    }
    return results;
}

static string ShortSha256(string content)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
    var sb = new StringBuilder(16);
    for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
    return sb.ToString();
}

// ---- Records ---------------------------------------------------------------

public sealed record EvalQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("expected_sub_agent")] string ExpectedSubAgent,
    [property: JsonPropertyName("expected_citation_set")] IReadOnlyList<string> ExpectedCitationSet,
    [property: JsonPropertyName("acceptable_refusal")] bool AcceptableRefusal,
    [property: JsonPropertyName("notes")] string? Notes = null);

public sealed record TitleHit(
    [property: JsonPropertyName("id")] string Id);

public sealed record TitleHitWithManufacturer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("manufacturer")] string? Manufacturer);

public sealed record TitleSidecarEntry(string? MachineTitle, string? ExpectedManufacturer);

public sealed record RecurationOutcome(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("curated_title")] string? CuratedTitle,
    [property: JsonPropertyName("expected_manufacturer")] string? ExpectedManufacturer,
    [property: JsonPropertyName("resolved_manufacturer")] string? ResolvedManufacturer,
    [property: JsonPropertyName("resolved_opdb_id")] string? ResolvedOpdbId,
    [property: JsonPropertyName("status")] string Status);

public sealed record RecurationCounts(
    [property: JsonPropertyName("processed")] int Processed,
    [property: JsonPropertyName("recurated")] int Recurated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("skipped_out_of_scope")] int SkippedOutOfScope,
    [property: JsonPropertyName("skipped_no_match")] int SkippedNoMatch,
    [property: JsonPropertyName("skipped_mfg_mismatch")] int SkippedMfgMismatch,
    [property: JsonPropertyName("manufacturer_unconstrained")] int ManufacturerUnconstrained);

public sealed record RecurationManifest(
    [property: JsonPropertyName("recurated_at_utc")] string RecuratedAtUtc,
    [property: JsonPropertyName("cosmos_endpoint")] string CosmosEndpoint,
    [property: JsonPropertyName("cosmos_database")] string CosmosDatabase,
    [property: JsonPropertyName("cosmos_container")] string CosmosContainer,
    [property: JsonPropertyName("jsonl_sha256_before")] string JsonlSha256Before,
    [property: JsonPropertyName("script_sha256")] string ScriptSha256,
    [property: JsonPropertyName("counts")] RecurationCounts Counts,
    [property: JsonPropertyName("outcomes")] IReadOnlyList<RecurationOutcome> Outcomes);
