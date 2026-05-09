using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Observability;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to all four agents per ADR-0014.
// Sibling to MachineGroundingTool — the latter grounds via OPDB on
// machine identity; this one grounds via AI Search RAG retrieval on
// document chunks (manuals, service bulletins, metadata cards) per
// ADR-0021 + ADR-0022.
//
// The Microsoft Agent Framework's AIFunctionFactory.Create wraps the
// SearchCorpusAsync method into an AIFunction with auto-generated
// JSON-Schema (from [Description] attributes on the method + its
// arguments). Sub-agent prompts (Repair / Rules / Valuation) and the
// Wizard prompt teach the model when to call it.
//
// Failure posture (ADR-0023): a transport-level exception from the
// retriever is caught and surfaced as an EMPTY result rather than
// rethrown. The empty result naturally drives the citation-required
// guardrail (W4-3) to refuse with category=NoCitation rather than
// fabricate. Re-throwing here would let the model loop on transient
// failures (Microsoft Agent Framework retries function calls), so we
// fail closed at the tool boundary and let the orchestrator's
// guardrail handle the user-visible refusal. The catch block emits
// `pinwiz.ai.tool_errors_total{tool=searchCorpus}` per ADR-0023 §
// Negative consequence #3 so an operator can distinguish "model
// didn't call the tool" refusals from "tool threw" refusals — both
// produce empty citation sets but they need different alerts. The
// counter is defined on `PinballWizardTelemetry.AiToolErrors`.
public sealed class SearchCorpusTool
{
    // Server-side TopK ceiling. The model can request up to this; a
    // hostile prompt asking for `topK=1000` is clamped here so it
    // can't pull the entire index per call. 20 is empirically a
    // generous ceiling for chunk-grounded answers; the citation
    // surface starts to feel diluted past ~10.
    internal const int TopKCeiling = 20;
    internal const int TopKDefault = 8;

    // Tool tag value emitted on `pinwiz.ai.tool_duration_ms` and
    // `pinwiz.ai.tool_errors_total`. Matches the JSON-Schema function
    // name the Microsoft Agent Framework derives from this method, so
    // dashboards, prompts, and the MachineGroundingTool sibling all
    // agree on the label.
    internal const string ToolTagValue = "searchCorpus";

    private readonly IRagRetriever _retriever;
    private readonly ILogger<SearchCorpusTool> _logger;

    public SearchCorpusTool(IRagRetriever retriever, ILogger<SearchCorpusTool> logger)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(logger);
        _retriever = retriever;
        _logger = logger;
    }

    [Description("Search the indexed pinball-machine corpus (manuals, service bulletins, metadata cards) for chunks relevant to a question. Returns up to topK page-anchored chunks with document URLs you must cite. Returns an empty list if nothing matches — when empty, do not fabricate; refuse instead.")]
    public async Task<SearchCorpusResult> SearchCorpusAsync(
        [Description("The natural-language question or query to search the corpus with. Pass the user's question through unchanged unless you need to scope it to a specific machine or document type.")] string query,
        [Description("Optional: constrain results to a specific machine by OPDB ID (for example: 'GRBNN-MQERZ'). Use this when the user has already identified the machine via getMachineByTitle and you want manual/bulletin chunks for that specific machine.")] string? machineId,
        [Description("Optional: constrain to a document type. Allowed values: 'manual', 'service_bulletin', 'metadata_card'. Omit for unfiltered.")] string? documentType,
        [Description("Optional: maximum number of chunks to return. Default 8; max 20.")] int? topK,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // Mirror MachineGroundingTool's whitespace-input posture:
            // return empty rather than throwing, so a confused agent
            // loop can't take down the orchestrator. The empty result
            // hits the citation-required guardrail naturally.
            _logger.LogDebug("SearchCorpusTool: empty query, returning empty result.");
            return new SearchCorpusResult([]);
        }

        // Outer Stopwatch + try/finally measures per-tool latency
        // including the catch path (ADR-0023 fail-closed-on-transport-
        // error path). Failure latency is operationally meaningful —
        // a slow-then-empty response and a fast-then-empty response
        // need different alerts. The retrieval-only timing lives on
        // `pinwiz.rag.retrieval_duration_ms` separately so dashboards
        // can subtract retrieval latency from tool latency to surface
        // tool-side overhead drift.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var clampedTopK = ClampTopK(topK);
            var options = new RetrievalOptions(
                TopK: clampedTopK,
                MachineId: NormalizeOptional(machineId),
                DocumentType: NormalizeOptional(documentType));

            IReadOnlyList<RetrievedChunk> chunks;
            try
            {
                chunks = await _retriever
                    .RetrieveAsync(query, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the caller's intent — propagate.
                throw;
            }
            catch (Exception ex)
            {
                // Transport-level failures (auth, network, AI Search 5xx)
                // are caught + surfaced as empty so the model can't loop
                // on a transient failure. The tool-errors counter (per
                // ADR-0023 § Negative consequence #3) tags by `tool` so
                // operator dashboards distinguish retrieval-side failures
                // from agent-didn't-call-tool cases — both produce empty
                // citation sets but they need different alerts.
                PinballWizardTelemetry.AiToolErrors.Add(1,
                    new KeyValuePair<string, object?>("tool", ToolTagValue));
                _logger.LogWarning(
                    ex,
                    "SearchCorpusTool: retriever threw — returning empty result. query length={QueryLength} machineId={MachineId} documentType={DocumentType} topK={TopK}",
                    query.Length,
                    options.MachineId ?? "(any)",
                    options.DocumentType ?? "(any)",
                    options.TopK);
                return new SearchCorpusResult([]);
            }

            var hits = new List<SearchCorpusHit>(capacity: chunks.Count);
            foreach (var chunk in chunks)
            {
                hits.Add(new SearchCorpusHit(
                    MachineId: chunk.MachineId,
                    MachineTitle: chunk.MachineTitle,
                    DocumentId: chunk.DocumentId,
                    DocumentUrl: chunk.DocumentUrl,
                    DocumentType: chunk.DocumentType,
                    PageStart: chunk.PageStart,
                    PageEnd: chunk.PageEnd,
                    SectionHeading: chunk.SectionHeading,
                    Content: chunk.Content)
                {
                    // Score is threaded through [JsonIgnore] so the model
                    // does not see it; the citation extractor reads it to
                    // populate Citation.RelevanceScore (PR-C2). RetrievedChunk
                    // guarantees a non-null double (AiSearchRagRetriever
                    // resolves reranker → BM25 → 0.0 before constructing it),
                    // so the cast to double? is always value-present.
                    Score = chunk.Score,
                    // LastScrapedUtc is threaded through [JsonIgnore] (PR-C3)
                    // so the model never sees it. The citation extractor reads
                    // it to populate Citation.LastScrapedUtc for the frontend
                    // freshness badge (ADR-0026 § 4). Null for chunks indexed
                    // before PR-C3 — the frontend badge is conditionally rendered.
                    LastScrapedUtc = chunk.LastScrapedUtc,
                });
            }

            _logger.LogDebug(
                "SearchCorpusTool: query length={QueryLength} hits={HitCount} machineId={MachineId} documentType={DocumentType} topK={TopK}",
                query.Length,
                hits.Count,
                options.MachineId ?? "(any)",
                options.DocumentType ?? "(any)",
                options.TopK);

            return new SearchCorpusResult(hits);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", ToolTagValue));
        }
    }

    internal static int ClampTopK(int? requested)
    {
        if (requested is null || requested <= 0)
        {
            return TopKDefault;
        }
        return Math.Min(requested.Value, TopKCeiling);
    }

    // Normalize "" / "  " to null so the retriever's filter builder
    // doesn't emit an `eq ''` clause that excludes every legitimate
    // value. Matches AiSearchRagRetriever.BuildFilter's empty-as-absent
    // behavior — both ends of the contract agree on what "not set"
    // looks like.
    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
