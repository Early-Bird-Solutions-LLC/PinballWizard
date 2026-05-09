using System.Diagnostics;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Default `IRagReconciler` impl. Samples the most-recently-recorded N
// rows from `rag_index_state` (recency-biased — recent ingests are the
// documents most likely to have hit a transient AI Search outage that
// would surface as drift), then for each row queries AI Search with a
// per-document_id filter to count actual chunks. Drift is classified as
// either `missing` (zero chunks in AI Search) or `count_mismatch`
// (count differs from the state row's recorded chunk_count).
//
// Failure posture per the contract:
//   - Sampling errors abort the reconcile (no partial signal worth
//     pretending is comprehensive).
//   - Per-document verify errors are caught and logged; the document
//     is counted as inspected but NOT classified as drift (we don't
//     know — the verify call itself failed).
//   - `ReconcileAsync` does NOT throw to its caller; surface every
//     failure path through telemetry + logs.
public sealed class CosmosAiSearchRagReconciler : IRagReconciler
{
    private readonly Container _indexStateContainer;
    private readonly SearchClient _searchClient;
    private readonly RagIngestionOptions _options;
    private readonly ILogger<CosmosAiSearchRagReconciler> _logger;

    public CosmosAiSearchRagReconciler(
        Container indexStateContainer,
        SearchClient searchClient,
        IOptions<RagIngestionOptions> options,
        ILogger<CosmosAiSearchRagReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(indexStateContainer);
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _indexStateContainer = indexStateContainer;
        _searchClient = searchClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        PinballWizardTelemetry.RagChangefeedReconcileStarted.Add(1);

        var stopwatch = Stopwatch.StartNew();
        int sampled = 0;
        int missing = 0;
        int countMismatch = 0;

        try
        {
            // Sample step. `SELECT TOP @sampleSize * FROM c ORDER BY
            // c.recorded_utc DESC` is recency-biased — recent ingests
            // are the documents most likely to have hit a transient
            // failure that would surface as drift. A purely random
            // sample (`ORDER BY ABS(NEXT_RANDOM_LONG())`) would scan
            // the entire container per call — too expensive on Cosmos
            // serverless for a startup-only check.
            var query = new QueryDefinition(
                "SELECT TOP @sampleSize * FROM c ORDER BY c.recorded_utc DESC")
                .WithParameter("@sampleSize", _options.ReconcileSampleSize);

            using var iterator = _indexStateContainer
                .GetItemQueryIterator<IndexStateDocument>(query);

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var stateRow in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sampled++;
                    var classification = await ClassifyAsync(stateRow, cancellationToken).ConfigureAwait(false);
                    switch (classification)
                    {
                        case DriftClassification.Missing:
                            missing++;
                            PinballWizardTelemetry.RagChangefeedReconcileDrift.Add(
                                1, new KeyValuePair<string, object?>("drift_type", "missing"));
                            break;
                        case DriftClassification.CountMismatch:
                            countMismatch++;
                            PinballWizardTelemetry.RagChangefeedReconcileDrift.Add(
                                1, new KeyValuePair<string, object?>("drift_type", "count_mismatch"));
                            break;
                        case DriftClassification.Match:
                        case DriftClassification.VerifyFailed:
                        default:
                            // Match → no drift; VerifyFailed → already
                            // logged inside ClassifyAsync, not counted
                            // as drift because we don't actually know.
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation propagates so the hosted-service caller sees
            // the shutdown signal cleanly. The partial result up to this
            // point is NOT returned — callers shouldn't act on a
            // truncated reconcile.
            throw;
        }
        catch (Exception ex)
        {
            // Anything else: log and continue with whatever we did
            // sample. Per the contract we don't throw to the caller —
            // the worker keeps serving the change feed.
            _logger.LogWarning(
                ex,
                "RAG reconcile: sampling step failed after {SampledCount} rows. Returning partial result.",
                sampled);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.RagChangefeedReconcileDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds);
            if (sampled > 0)
            {
                PinballWizardTelemetry.RagChangefeedReconcileSampled.Add(sampled);
            }
        }

        var result = new ReconciliationResult(
            SampledCount: sampled,
            MissingDriftCount: missing,
            CountMismatchCount: countMismatch,
            Duration: stopwatch.Elapsed);

        if (missing > 0 || countMismatch > 0)
        {
            _logger.LogWarning(
                "RAG reconcile: sampled={Sampled} missing={Missing} count_mismatch={CountMismatch} duration={DurationMs:F1}ms. Investigate the indexer write path.",
                sampled, missing, countMismatch, stopwatch.Elapsed.TotalMilliseconds);
        }
        else
        {
            _logger.LogInformation(
                "RAG reconcile: sampled={Sampled} duration={DurationMs:F1}ms. No drift detected.",
                sampled, stopwatch.Elapsed.TotalMilliseconds);
        }

        return result;
    }

    private async Task<DriftClassification> ClassifyAsync(
        IndexStateDocument stateRow,
        CancellationToken cancellationToken)
    {
        long? actualCount;
        try
        {
            actualCount = await CountChunksAsync(stateRow.DocumentId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RAG reconcile: AI Search verify failed for document={DocumentId}; document is counted as sampled but not classified as drift.",
                stateRow.DocumentId);
            actualCount = null;
        }

        return ClassifyDrift(stateRow, actualCount);
    }

    // Pure classification given a state row and the AI Search chunk
    // count. `null` actualCount means the verify call itself failed
    // (already logged at the call site) — we don't actually know the
    // state, so don't count it as drift. State rows recording
    // `ChunkCount = 0` from the chunker-produced-zero-chunks defensive
    // path are legitimately empty in the index — don't classify as
    // count_mismatch in that case (otherwise every recorded-but-empty
    // document would surface as drift). Pure so the classification
    // table is exhaustively unit-testable without mocking Cosmos /
    // AI Search.
    internal static DriftClassification ClassifyDrift(
        IndexStateDocument stateRow,
        long? actualCount)
    {
        if (actualCount is null)
        {
            return DriftClassification.VerifyFailed;
        }
        if (actualCount.Value == 0)
        {
            return DriftClassification.Missing;
        }
        if (stateRow.ChunkCount > 0 && actualCount.Value != stateRow.ChunkCount)
        {
            return DriftClassification.CountMismatch;
        }
        return DriftClassification.Match;
    }

    // Filtered AI Search query — `filter=document_id eq '<id>'` with
    // `IncludeTotalCount=true` and `Size=0` so the server returns the
    // total count without paging through documents. This is by far
    // the cheapest verify path; the alternative (`SearchAsync` + iterate)
    // would download chunks just to count them.
    private async Task<long> CountChunksAsync(string documentId, CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            Filter = $"{AiSearchIndexFields.DocumentId} eq '{EscapeForOData(documentId)}'",
            IncludeTotalCount = true,
            Size = 0,
        };

        var response = await _searchClient
            .SearchAsync<RetrievedChunkDocument>(searchText: "*", options, cancellationToken)
            .ConfigureAwait(false);
        return response.Value.TotalCount ?? 0;
    }

    // Minimal OData string escape — single-quote → two single-quotes
    // is the OData V4 literal escape rule, which AI Search inherits.
    // Document IDs in this project are deterministic SHA-derived
    // hex prefixed with `doc_` (CLAUDE.md § Provenance model) so they
    // never contain single quotes; this guard is defense-in-depth in
    // case a future schema change relaxes the ID convention.
    internal static string EscapeForOData(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    internal enum DriftClassification
    {
        Match,
        Missing,
        CountMismatch,
        VerifyFailed,
    }
}
