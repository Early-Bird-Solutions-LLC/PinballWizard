using System.Diagnostics;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Findability;
using PinballWizard.Application.Observability;
using PinballWizard.Infrastructure.Rag.Indexing;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// ADR-0049 phase 2b: queries the machine findability index (pinwiz-machines-v1)
// to resolve getMachineByTitle queries that miss all Cosmos point-read paths.
//
// QUERY STRATEGY — single simple query, no Lucene fuzzy:
//   QueryType=Simple, SearchFields=[title, title_prefix, title_phonetic],
//   ScoringProfile=machine-content-intrinsic.
//
//   One query covers all five findability categories:
//     - Synonyms / abbreviations : synonym map fires on `title` under Simple
//       ("MM"→"Medieval Madness", "AFM"→"Attack from Mars", "Wonka"→Willy Wonka)
//     - Partial / subtitle       : BM25 on `title` matches substrings
//       ("Chocolate Factory"→"Willy Wonka …", "Legacy of the Beast"→"Iron Maiden…")
//     - Prefix / typeahead       : edge-n-gram `title_prefix` matches prefixes
//       ("Medie"→"Medieval Madness")
//     - Phonetic typos           : doubleMetaphone `title_phonetic` matches homophones
//       ("Godzila"→"Godzilla", "Houdinni"→"Houdini", "Mideval Madness"→"Medieval Madness")
//     - Content-intrinsic ranking: scoring-profile magnitude(completeness)+freshness
//       ranks richer records higher when text relevance ties ("Godzilla"→Stern 2021>Sega 1998)
//
//   Lucene fuzzy (`term~1`, QueryType=Full) was evaluated against the live
//   pinwiz-machines-v1 index (2,160 docs on 2026-07-01) and rejected: it returned
//   zero results for single-word typo queries and does NOT co-apply with synonym
//   maps under full-Lucene syntax. The phonetic field already covers common English
//   typos far more reliably without the synonym-conflict issue.
//
// Observability mirrors AiSearchRagRetriever: one histogram per call, error counter
// on transport failures. The calling layer (MachineGroundingTool) emits its own
// per-tool histogram; this instrument tracks the AI Search latency slice only.
public sealed class AiSearchMachineIndex : IMachineSearchIndex
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AiSearchMachineIndex> _logger;

    public AiSearchMachineIndex(
        SearchClient searchClient,
        ILogger<AiSearchMachineIndex> logger)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(logger);
        _searchClient = searchClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MachineSearchHit>> SearchAsync(
        string query,
        int top,
        string? manufacturerKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = BuildSearchOptions(top, manufacturerKey);

            // SearchAsync<T> deserializes the selected fields from the index
            // response into MachineSearchResultDocument via STJ. The generic
            // overload is typed to the result document so the SDK handles
            // field projection automatically — no manual JSON traversal.
            var response = await _searchClient
                .SearchAsync<MachineSearchResultDocument>(query, options, cancellationToken)
                .ConfigureAwait(false);

            var hits = new List<MachineSearchHit>(capacity: top);
            await foreach (var result in response.Value.GetResultsAsync().ConfigureAwait(false))
            {
                var hit = MapToHit(result.Document, result.Score ?? 0.0);
                if (hit is not null)
                    hits.Add(hit);
            }

            _logger.LogDebug(
                "Machine search: query='{Query}' top={Top} hits={HitCount}",
                LogSanitizer.ForLog(query), top, hits.Count);

            return hits;
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.MachineSearchDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    // Builds the SearchOptions for a simple-mode machine-index query.
    // Exposed as internal static so AiSearchMachineIndexTests can pin the
    // field selection, scoring profile, and query type without a live client.
    internal static SearchOptions BuildSearchOptions(int top, string? manufacturerKey)
    {
        var options = new SearchOptions
        {
            // Simple query type: enables BM25 on `title` (with synonym-map expansion),
            // edge-n-gram on `title_prefix` (prefix/typeahead), and doubleMetaphone on
            // `title_phonetic` (phonetic typo tolerance). The three-field combination
            // handles all five findability categories in a single round-trip.
            // QueryType is Simple (the default) — stated explicitly so intent is clear.
            QueryType = SearchQueryType.Simple,

            // Scoring profile "machine-content-intrinsic": magnitude(completeness) +
            // freshness(last_updated_utc) boosts richer, more recently synced records.
            // Applied on top of BM25 relevance so tie-breaking is content-driven, not
            // insertion-order (matches the Phase 1 tie-break goal from ADR-0049).
            ScoringProfile = MachineSearchIndexSchema.ScoringProfileName,

            Size = top,

            Select =
            {
                MachineSearchIndexFields.Id,
                MachineSearchIndexFields.Title,
                MachineSearchIndexFields.Manufacturer,
                MachineSearchIndexFields.ManufacturerKey,
                MachineSearchIndexFields.GroupId,
                MachineSearchIndexFields.Year,
            },

            SearchFields =
            {
                // title: standard analyzer + synonym map — abbreviations, full-text BM25.
                MachineSearchIndexFields.Title,

                // title_prefix: edge-n-gram — prefix/typeahead without wildcard syntax.
                MachineSearchIndexFields.TitlePrefix,

                // title_phonetic: doubleMetaphone — phonetic typo tolerance.
                MachineSearchIndexFields.TitlePhonetic,
            },
        };

        if (!string.IsNullOrWhiteSpace(manufacturerKey))
        {
            // OData string-literal escaping: a single quote is doubled.
            var escaped = manufacturerKey.Replace("'", "''", StringComparison.Ordinal);
            options.Filter = $"{MachineSearchIndexFields.ManufacturerKey} eq '{escaped}'";
        }

        return options;
    }

    // Maps a single AI Search result document to a MachineSearchHit.
    // Returns null when the document lacks a required identity field (OpdbId or
    // ManufacturerKey) — defensive against malformed index documents without
    // throwing so a single bad row doesn't abort the whole result list.
    // Exposed internal static for unit-test pinning of the field mapping.
    internal static MachineSearchHit? MapToHit(MachineSearchResultDocument doc, double score)
    {
        if (string.IsNullOrEmpty(doc.Id) || string.IsNullOrEmpty(doc.ManufacturerKey))
            return null;

        return new MachineSearchHit(
            OpdbId: doc.Id,
            Title: doc.Title,
            ManufacturerDisplayName: doc.Manufacturer,
            ManufacturerKey: doc.ManufacturerKey,
            GroupId: doc.GroupId,
            Year: doc.Year,
            Score: score);
    }
}
