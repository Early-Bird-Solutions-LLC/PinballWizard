#nullable enable
// probe-scoring-profile-hybrid.csx
// SPIKE: Does a scoring profile affect ranking in HYBRID (text+vector) queries?
//
// Run:  $env:AZURE_CONFIG_DIR="$env:USERPROFILE\.azure-pinwiz"
//       $env:AZURE_TOKEN_CREDENTIALS="dev"
//       dotnet script tools/probe-scoring-profile-hybrid.csx
//
// Safety: ONLY touches throwaway index "pinwiz-findability-spike-v1".
//         Production index "pinwiz-rag-v1" is never touched.
//         Index is deleted in finally{} — even on failure.

#r "nuget: Azure.Search.Documents, 12.0.0"
#r "nuget: Azure.Identity, 1.21.0"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

const string SearchEndpoint = "https://pinwiz-search-dev-buutj.search.windows.net";
const string SpikeIndex     = "pinwiz-findability-spike-v1";

var endpoint    = new Uri(SearchEndpoint);
var credential  = new DefaultAzureCredential();
var indexClient = new SearchIndexClient(endpoint, credential);

// Helper: run a search and return (id, score) list in rank order
static async Task<List<(string id, double score)>> Query(
    SearchClient client,
    string searchText,
    string? scoringProfile,
    float[]? vector)
{
    var opts = new SearchOptions
    {
        Select          = { "id", "content", "quality" },
        Size            = 10,
        ScoringProfile  = scoringProfile
    };

    if (vector is not null)
    {
        var vq = new VectorizedQuery(new ReadOnlyMemory<float>(vector))
        {
            KNearestNeighborsCount = 5
        };
        vq.Fields.Add("vec");
        opts.VectorSearch = new VectorSearchOptions();
        opts.VectorSearch.Queries.Add(vq);
    }

    var response = await client.SearchAsync<SearchDocument>(searchText, opts);
    var results  = new List<(string id, double score)>();
    await foreach (var hit in response.Value.GetResultsAsync())
        results.Add((hit.Document["id"].ToString()!, hit.Score!.Value));
    return results;
}

static int Quality(string id) => id switch { "alpha" => 1, "beta" => 100, "gamma" => 2, "delta" => 3, "epsilon" => 4, _ => 0 };

Console.WriteLine("=== PinballWizard AI Search Scoring-Profile-on-Hybrid Spike ===");
Console.WriteLine($"Index: {SpikeIndex}");
Console.WriteLine($"Endpoint: {SearchEndpoint}");

try
{
    // ── 1. Vector search infrastructure ─────────────────────────────────────
    var vectorSearch = new VectorSearch();
    vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("vec-hnsw")
    {
        Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine }
    });
    vectorSearch.Profiles.Add(new VectorSearchProfile("vec-profile", "vec-hnsw"));

    // ── 2. Scoring profile: magnitude boost on 'quality' (0–100), boost=50 ──
    //    A field used by a magnitude function MUST be filterable (and numeric).
    var magnitudeParams = new MagnitudeScoringParameters(
        boostingRangeStart: 0,
        boostingRangeEnd:   100)
    {
        ShouldBoostBeyondRangeByConstant = true
    };
    var magnitudeFunc = new MagnitudeScoringFunction(
        fieldName:  "quality",
        boost:       50.0,
        parameters:  magnitudeParams)
    {
        Interpolation = ScoringFunctionInterpolation.Linear
    };
    var scoringProfile = new ScoringProfile("boostQuality");
    scoringProfile.Functions.Add(magnitudeFunc);

    // ── 3. Create throwaway index ────────────────────────────────────────────
    var fields = new List<SearchField>
    {
        new SimpleField("id",      SearchFieldDataType.String) { IsKey = true },
        new SearchableField("content"),                               // standard.lucene default
        new SimpleField("quality", SearchFieldDataType.Double) { IsFilterable = true },
        new SearchField("vec",     SearchFieldDataType.Collection(SearchFieldDataType.Single))
        {
            IsSearchable          = true,
            VectorSearchDimensions    = 3,
            VectorSearchProfileName   = "vec-profile"
        }
    };

    var index = new SearchIndex(SpikeIndex, fields)
    {
        VectorSearch    = vectorSearch,
        ScoringProfiles = { scoringProfile }
    };

    Console.WriteLine("\nCreating index...");
    await indexClient.CreateOrUpdateIndexAsync(index);
    Console.WriteLine("Index created.");

    var searchClient = indexClient.GetSearchClient(SpikeIndex);

    // ── 4. Upload documents ──────────────────────────────────────────────────
    //
    // Every doc contains "wizard" so ALL get a BM25 baseline keyword score.
    //
    // | id      | keyword relevance | vec sim to [1,0,0] | quality |
    // |---------|-------------------|--------------------|---------|
    // | alpha   | highest (4 terms) | 1.00  (exact)      |   1  ← LOW  |
    // | beta    | medium  (3 terms) | 0.00  (orthogonal) | 100  ← HIGH |
    // | gamma   | medium  (3 terms) | 0.70  (partial)    |   2     |
    // | delta   | medium  (3 terms) | 0.00  (orthogonal) |   3     |
    // | epsilon | medium  (3 terms) | ~0.50 (partial)    |   4     |
    //
    // Design intent: 'alpha' naturally tops both keyword and vector legs.
    // If the scoring profile (boost=50 on quality=100 for 'beta') fires on
    // hybrid queries, 'beta' will climb dramatically — unmistakable signal.

    var docs = new SearchDocument[]
    {
        new() { ["id"] = "alpha",   ["content"] = "wizard guide rules pinball strategy", ["quality"] = 1.0,   ["vec"] = new float[] { 1f,   0f,   0f   } },
        new() { ["id"] = "beta",    ["content"] = "wizard help documentation reference",  ["quality"] = 100.0, ["vec"] = new float[] { 0f,   0f,   1f   } },
        new() { ["id"] = "gamma",   ["content"] = "wizard setup instructions manual",     ["quality"] = 2.0,   ["vec"] = new float[] { 0.7f, 0.3f, 0f   } },
        new() { ["id"] = "delta",   ["content"] = "wizard tips tricks techniques",        ["quality"] = 3.0,   ["vec"] = new float[] { 0f,   1f,   0f   } },
        new() { ["id"] = "epsilon", ["content"] = "wizard overview feature summary",      ["quality"] = 4.0,   ["vec"] = new float[] { 0.5f, 0.1f, 0.4f } },
    };

    await searchClient.UploadDocumentsAsync(docs);
    Console.WriteLine("5 documents uploaded. Waiting 4s for indexing...");
    await Task.Delay(4000);

    // ── 5a. KEYWORD BASELINE (confirm profile works at all) ─────────────────
    Console.WriteLine("\n══════════════════════════════════════════════════════");
    Console.WriteLine("SECTION A — KEYWORD BASELINE (profile sanity check)");
    Console.WriteLine("══════════════════════════════════════════════════════");

    var kwNoProfile   = await Query(searchClient, "wizard", null,            null);
    var kwWithProfile = await Query(searchClient, "wizard", "boostQuality",  null);

    Console.WriteLine("\nKeyword 'wizard' WITHOUT scoringProfile:");
    foreach (var (id, score) in kwNoProfile)
        Console.WriteLine($"  #{kwNoProfile.IndexOf((id,score))+1} [{id}] quality={Quality(id),3}  score={score:F4}");

    Console.WriteLine("\nKeyword 'wizard' WITH scoringProfile=boostQuality:");
    foreach (var (id, score) in kwWithProfile)
        Console.WriteLine($"  #{kwWithProfile.IndexOf((id,score))+1} [{id}] quality={Quality(id),3}  score={score:F4}");

    bool kwChanged = kwNoProfile[0].id != kwWithProfile[0].id;
    Console.WriteLine($"\n  Baseline verdict: scoring profile on keyword = {(kwChanged ? "RANKING CHANGED ✓ (profile is live)" : "NO CHANGE ✗ — profile may be misconfigured")}");

    // ── 5b. HYBRID (PRIMARY QUESTION) ───────────────────────────────────────
    Console.WriteLine("\n══════════════════════════════════════════════════════");
    Console.WriteLine("SECTION B — HYBRID (text 'wizard' + vector [1,0,0])");
    Console.WriteLine("══════════════════════════════════════════════════════");

    float[] queryVec = [1f, 0f, 0f]; // strongly favors 'alpha'

    var hybNoProfile   = await Query(searchClient, "wizard", null,           queryVec);
    var hybWithProfile = await Query(searchClient, "wizard", "boostQuality", queryVec);

    Console.WriteLine("\nHybrid WITHOUT scoringProfile:");
    foreach (var (id, score) in hybNoProfile)
        Console.WriteLine($"  #{hybNoProfile.IndexOf((id,score))+1} [{id}] quality={Quality(id),3}  score={score:F6}");

    Console.WriteLine("\nHybrid WITH scoringProfile=boostQuality:");
    foreach (var (id, score) in hybWithProfile)
        Console.WriteLine($"  #{hybWithProfile.IndexOf((id,score))+1} [{id}] quality={Quality(id),3}  score={score:F6}");

    int alphaRankBefore = hybNoProfile.FindIndex(x => x.id == "alpha") + 1;
    int alphaRankAfter  = hybWithProfile.FindIndex(x => x.id == "alpha") + 1;
    int betaRankBefore  = hybNoProfile.FindIndex(x => x.id == "beta") + 1;
    int betaRankAfter   = hybWithProfile.FindIndex(x => x.id == "beta") + 1;

    Console.WriteLine($"\n  Movement summary:");
    Console.WriteLine($"    'alpha' (best text+vec, quality=1):   rank #{alphaRankBefore} → #{alphaRankAfter}");
    Console.WriteLine($"    'beta'  (weak text+vec, quality=100): rank #{betaRankBefore} → #{betaRankAfter}");

    // Verdict: profile affects hybrid if beta climbed OR alpha fell after adding the profile
    bool hybridAffected = betaRankAfter < betaRankBefore || alphaRankAfter > alphaRankBefore;
    string primaryVerdict = hybridAffected
        ? $"SCORING PROFILE ON HYBRID: AFFECTS RANKING  (beta #{betaRankBefore}→#{betaRankAfter}, alpha #{alphaRankBefore}→#{alphaRankAfter})"
        : "SCORING PROFILE ON HYBRID: NO EFFECT  (ranks identical with/without profile)";

    Console.WriteLine($"\n>>> PRIMARY VERDICT: {primaryVerdict} <<<");

    // ── 5c. SECONDARY: Lucene fuzzy ──────────────────────────────────────────
    Console.WriteLine("\n══════════════════════════════════════════════════════");
    Console.WriteLine("SECTION C — SECONDARY: Lucene fuzzy query");
    Console.WriteLine("══════════════════════════════════════════════════════");

    // "wizrd" has edit distance 1 from "wizard" (one deletion).
    var fuzzyOpts = new SearchOptions { QueryType = SearchQueryType.Full, Select = { "id" } };
    var fuzzyResp = await searchClient.SearchAsync<SearchDocument>("content:wizrd~1", fuzzyOpts);
    var fuzzyHits = new List<string>();
    await foreach (var hit in fuzzyResp.Value.GetResultsAsync())
        fuzzyHits.Add(hit.Document["id"].ToString()!);

    Console.WriteLine($"\nFuzzy 'content:wizrd~1' (1-edit typo of 'wizard'): hits=[{string.Join(", ", fuzzyHits)}]");
    bool fuzzyHitAll = fuzzyHits.Count == 5;
    Console.WriteLine($"  All 5 docs matched: {(fuzzyHitAll ? "YES" : "NO — got " + fuzzyHits.Count)}");
    Console.WriteLine($"  Secondary verdict: Lucene fuzzy ~1 {(fuzzyHits.Any() ? "MATCHES" : "DOES NOT MATCH")} 1-edit typos on keyword fields");
}
finally
{
    Console.WriteLine("\n══════════════════════════════════════════════════════");
    Console.WriteLine("CLEANUP");
    Console.WriteLine("══════════════════════════════════════════════════════");
    Console.WriteLine($"Deleting '{SpikeIndex}'...");
    try
    {
        await indexClient.DeleteIndexAsync(SpikeIndex, cancellationToken: default);
        // Confirm deletion
        try
        {
            await indexClient.GetIndexAsync(SpikeIndex);
            Console.WriteLine("WARNING: Index still present after delete call — manual cleanup required!");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Console.WriteLine($"Confirmed: '{SpikeIndex}' is gone (404). Cleanup complete.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR during cleanup: {ex.Message}");
        Console.WriteLine($"MANUAL ACTION REQUIRED: delete index '{SpikeIndex}' in Azure portal.");
    }
}
