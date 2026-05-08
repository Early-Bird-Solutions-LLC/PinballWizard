using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PinballWizard.Application.Observability;

// Single Meter + ActivitySource for the whole project. Phase 3 / 4 / 5
// services add their counters/activities to this Meter under different
// instrument-name prefixes (pinwiz.scrape.*, pinwiz.rag.*, etc.).
// Concentrating instrumentation into one Meter lets ServiceDefaults
// register it once via AddMeter(MeterName) and have all metrics flow.
// See docs/observability.md for the full inventory and the extension
// pattern when adding new instruments.
public static class PinballWizardTelemetry
{
    public const string MeterName = "PinballWizard";
    public const string ActivitySourceName = "PinballWizard";

    public static readonly Meter Meter = new(MeterName, "1.0.0");
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    // ── OPDB sync instrumentation ────────────────────────────────────────
    // All counters carry a `mode` attribute (apply | dry_run) so dashboards
    // can filter dry-run projections out of operational dashboards while
    // still observing them in pre-deploy validation runs.

    public static readonly Counter<long> OpdbSyncFetched = Meter.CreateCounter<long>(
        "pinwiz.opdb.sync.fetched",
        unit: "{record}",
        description: "OPDB records fetched from the API across all sync runs.");

    public static readonly Counter<long> OpdbSyncInserted = Meter.CreateCounter<long>(
        "pinwiz.opdb.sync.inserted",
        unit: "{machine}",
        description: "Machines newly inserted into the repository (or projected-insert in dry-run mode).");

    public static readonly Counter<long> OpdbSyncUpdated = Meter.CreateCounter<long>(
        "pinwiz.opdb.sync.updated",
        unit: "{machine}",
        description: "Existing machines updated with merged OPDB fields (or projected-update in dry-run mode).");

    public static readonly Counter<long> OpdbSyncSkipped = Meter.CreateCounter<long>(
        "pinwiz.opdb.sync.skipped",
        unit: "{record}",
        description: "OPDB records skipped because they failed validation or mapping.");

    public static readonly Counter<long> OpdbSyncFailed = Meter.CreateCounter<long>(
        "pinwiz.opdb.sync.failed",
        unit: "{run}",
        description: "OPDB sync runs that aborted with an exception.");

    public static readonly Histogram<double> OpdbSyncDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.opdb.sync.duration_ms",
        unit: "ms",
        description: "Wall-clock duration of an OPDB sync run in milliseconds.");

    // ── Pinball Map fetch instrumentation ────────────────────────────────
    // Pinball Map fetches are per-region (1 fetch = 1 region's locations),
    // not bulk-streamed like OPDB. Counters carry a `cache_outcome`
    // attribute (hit | miss | refresh) so dashboards can observe how
    // often the on-disk cache spares the network.

    public static readonly Counter<long> PinballMapFetched = Meter.CreateCounter<long>(
        "pinwiz.pinballmap.fetched",
        unit: "{region}",
        description: "Pinball Map region-locations fetches across all calls (cache hits + misses).");

    public static readonly Counter<long> PinballMapLocations = Meter.CreateCounter<long>(
        "pinwiz.pinballmap.locations",
        unit: "{location}",
        description: "Locations returned by Pinball Map fetches. Useful for capacity planning vs API growth.");

    public static readonly Counter<long> PinballMapFailed = Meter.CreateCounter<long>(
        "pinwiz.pinballmap.failed",
        unit: "{fetch}",
        description: "Pinball Map fetches that aborted with an exception.");

    public static readonly Histogram<double> PinballMapFetchDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.pinballmap.fetch.duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single Pinball Map region fetch in milliseconds.");

    // ── AI orchestrator instrumentation (ADR-0015) ───────────────────────
    // Foundry's SDK auto-emits OTel spans on the Azure.AI.Projects.*
    // activity source (enabled in ServiceDefaults via the GenAI tracing
    // app-context switch) carrying gen_ai.* semantic-convention attributes
    // for token counts, per-call latency, and per-call model identity.
    // The pinwiz.ai.* instruments below add ONLY what auto-emission
    // doesn't cover — anything that lives in our IAiRouter wrapper above
    // the Foundry agents.

    public static readonly Counter<long> AiCacheHits = Meter.CreateCounter<long>(
        "pinwiz.ai.cache.hits",
        unit: "{question}",
        description: "User-questions answered from the in-process LRU semantic cache without invoking Foundry agents (per ADR-0015).");

    public static readonly Counter<long> AiCacheMisses = Meter.CreateCounter<long>(
        "pinwiz.ai.cache.misses",
        unit: "{question}",
        description: "User-questions that missed the cache and were dispatched to the Wizard agent.");

    public static readonly Counter<long> AiCostUsdCents = Meter.CreateCounter<long>(
        "pinwiz.ai.cost_usd_cents",
        unit: "USD-cents",
        description: "Estimated USD-cents accumulated by AI calls. Computed from token counts × AiOptions.PricingTable. Drives the per-call cost ceiling and the daily anomaly aggregation in docs/observability.md.");

    public static readonly Counter<long> AiRefusals = Meter.CreateCounter<long>(
        "pinwiz.ai.refusals",
        unit: "{question}",
        description: "User-questions that ended in a refusal. Tagged with refusal_category (InsufficientGrounding | OutOfScope | LowModelConfidence | CostCeilingHit | HarmfulContent) so dashboards can distinguish retrieval drift from out-of-scope from safety blocks (per ADR-0017).");

    public static readonly Counter<long> AiEscalations = Meter.CreateCounter<long>(
        "pinwiz.ai.escalations",
        unit: "{question}",
        description: "User-questions where the Wizard routed to a heavy-tier sub-agent (gpt-4.1) after the initial light-tier (gpt-4o-mini) result fell below the confidence threshold (per ADR-0015).");

    public static readonly Histogram<double> AiDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.ai.duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single user-question round-trip through IAiRouter (cache lookup + Foundry agent invocation + post-process). Complements per-call gen_ai.* durations from auto-emitted spans.");

    public static readonly Counter<long> AiCitationsExtracted = Meter.CreateCounter<long>(
        "pinwiz.ai.citations.extracted_total",
        unit: "{citation}",
        description: "Citations attached to a Wizard answer. Tagged with source (tool_trace | regex_legacy) per ADR-0022 — during the Phase 4 cutover both extractors run; tool_trace is the primary and regex_legacy runs in parallel for behavioral comparison. The relative counts surface drift before the H3 eval baseline rerun.");

    // ── Eval harness instrumentation (ADR-0016) ──────────────────────────
    // The Phase 3 evaluation harness emits these instruments; Phase 6
    // dashboards aggregate them as a "metric trajectory" surface alongside
    // the committed JSON results. Counters carry no required attributes —
    // a single eval run is the natural unit; per-evaluator scores live in
    // the committed JSON rather than as metrics (per-question-per-run
    // metric cardinality would explode unhelpfully).

    public static readonly Counter<long> EvalRuns = Meter.CreateCounter<long>(
        "pinwiz.eval.runs",
        unit: "{run}",
        description: "Evaluation harness runs that completed (regardless of pass/fail).");

    public static readonly Counter<long> EvalRunsFailed = Meter.CreateCounter<long>(
        "pinwiz.eval.runs.failed",
        unit: "{run}",
        description: "Evaluation harness runs that aborted with an exception before producing a result file.");

    public static readonly Counter<long> EvalQuestionsScored = Meter.CreateCounter<long>(
        "pinwiz.eval.questions.scored",
        unit: "{question}",
        description: "Per-question evaluations completed (success + per-question failure both increment).");

    public static readonly Counter<long> EvalEvaluatorRegistrations = Meter.CreateCounter<long>(
        "pinwiz.eval.evaluator.registrations",
        unit: "{registration}",
        description: "Custom evaluator versions upserted into the Foundry project. Idempotent on every harness run; the counter increments per registration attempt regardless of whether the version already existed.");

    public static readonly Histogram<double> EvalQuestionDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.eval.question.duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single eval question (IAiRouter dispatch + scoring) in milliseconds.");

    // ── RAG indexing + retrieval instrumentation (build-spec § Phase 4 ─
    // scope item 25, ADR-0021). The four `pinwiz.rag.*` instruments below
    // were promised in the observability spec when the indexer (W2-3) and
    // retriever (W3-3) were specified; both shipped without them. Wiring
    // them now closes the gap-closure half of the observability follow-up
    // tracked at memory/project_observability_followup_per_tool_metrics.md.
    //
    // The `pinwiz.search.index_size_bytes` + `index_documents_total` gauges
    // identified in the brainstorm half of that follow-up are deferred to
    // a future Phase 2 batch — they need a periodic sampler rather than
    // hot-path emission, so the wiring shape is different.

    public static readonly Histogram<double> RagIndexingDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.rag.indexing_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single `IRagIndexer.UpsertAsync` call — embed batch + AI Search upsert + per-batch result aggregation. Measured at the indexer's outer boundary, so it captures total user-felt latency including embed-TPM throttling. Useful for capacity planning at Phase 4.5 corpus scaling vs. the curated-subset baseline.");

    public static readonly Counter<long> RagIndexedChunks = Meter.CreateCounter<long>(
        "pinwiz.rag.indexed_chunks_total",
        unit: "{chunk}",
        description: "Chunks successfully upserted into the AI Search index. Tagged with `document_type` (Manual | ServiceBulletin | MetadataCard) so dashboards can break down ingestion volume by source type — Stern bulletins should ramp first, then non-Stern manuals as Phase 4.5 expansion lands. Per-doc-failure counts (length-exceeded, schema validation) surface as `IndexUpsertResult.Failures` and are NOT counted here; only successes increment.");

    public static readonly Histogram<double> RagRetrievalDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.rag.retrieval_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single `IRagRetriever.RetrieveAsync` call — query embedding + AI Search hybrid query + post-filter mapping. Measured at the retriever's outer boundary so it captures total user-felt retrieval latency (the §7.1 user-delight reference for the corpus-search path). Pair with `pinwiz.ai.tool_duration_ms` once the latter ships to see how much of the searchCorpus tool budget is retrieval vs. tool overhead.");

    public static readonly Histogram<double> RagRetrievalScoreDistribution = Meter.CreateHistogram<double>(
        "pinwiz.rag.retrieval_score_distribution",
        unit: "{score}",
        description: "Per-result re-rank or BM25 score sampled on every `IRagRetriever.RetrieveAsync` result. Tagged with `score_source` (`semantic` when AI Search semantic ranker engaged | `bm25` when fallback to keyword score | `fallback_zero` when both null). Surfaces drift between the eval-baseline retrieval distribution and production-traffic retrieval distribution — informs the ADR-0024 cross-encoder gate trigger and the ADR-0017 confidence-threshold recalibration window.");

    // ── Activity (trace) names ───────────────────────────────────────────

    public const string OpdbSyncActivity = "pinwiz.opdb.sync";
    public const string PinballMapFetchActivity = "pinwiz.pinballmap.fetch";
    public const string AiRouterActivity = "pinwiz.ai.router";
    public const string EvalRunActivity = "pinwiz.eval.run";
}
