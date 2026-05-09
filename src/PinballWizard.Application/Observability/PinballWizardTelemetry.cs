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

    public static readonly Counter<long> AiToolErrors = Meter.CreateCounter<long>(
        "pinwiz.ai.tool_errors_total",
        unit: "{call}",
        description: "Function-tool calls that surfaced an exception inside the tool's catch boundary (the tool returned an empty result rather than rethrowing — see ADR-0023 § Negative consequence #3). Tagged with `tool` (searchCorpus | getMachineByTitle). Distinguishes retrieval-side failures (which become NoCitation refusals) from agent-didn't-call-tool refusals — both can produce empty citation sets but operationally they need different alerts.");

    public static readonly Histogram<double> AiToolDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.ai.tool_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single Foundry function-tool invocation, measured at the tool's outer boundary (input normalization + downstream call + post-processing into the model-facing DTO). Tagged with `tool` (searchCorpus | getMachineByTitle). Pair with `pinwiz.rag.retrieval_duration_ms` to isolate per-tool overhead from retrieval-side latency on the searchCorpus path. Drives the §7.1 architecture-v2 user-delight revisit triggers (200ms p95 structured-records latency for getMachineByTitle, 500ms cold-start for searchCorpus). The brainstorm's `cache_state` tag was dropped: the LRU semantic cache (per ADR-0015) wraps IAiRouter ABOVE the tools, so when a tool fires the cache state is structurally always 'miss-path' — that signal lives on `pinwiz.ai.cache.{hits,misses}` instead.");

    // ── First-token latency (ADR-0026 § 7) ──────────────────────────────
    // Emitted by AiRouter.AnswerStreamingAsync on the FIRST non-empty
    // TextDelta yielded to the client — covers both cache-hit replay
    // (a single TextDelta representing the whole cached answer) and
    // live-stream paths (the first per-update TextDelta). A refusal that
    // fires before any TextDelta is recorded with outcome=refusal so
    // dashboards can distinguish "model never produced text" (slow
    // orchestration path, possible tool-loop storm) from "model produced
    // text but guardrail refused post-stream" (which still emits a normal
    // first-token sample).
    //
    // Tags:
    //   cache_state ∈ { hit, miss } — distinguishes cache-replay latency
    //     (sub-millisecond; the cached answer is already in memory) from
    //     live-stream latency (hundreds of milliseconds; includes Foundry
    //     round-trip + any tool loops). Keeps the two distributions from
    //     contaminating each other on a single histogram.
    //   outcome ∈ { streamed, refusal } — present only when the outcome is
    //     known at emission time. For the refusal-before-TextDelta path
    //     (429 catch or guardrail fires before any text arrives) this tag
    //     is set to "refusal"; for normal TextDelta paths it is "streamed"
    //     (omitted from the tag set when using the default happy path to
    //     keep cardinality minimal — callers opt in to the "refusal" tag
    //     explicitly).
    //
    // Drives the §7.1 user-delight revisit triggers:
    //   200ms p95 first-token-ms for structured-records latency.
    //   500ms cold-start cache trigger.

    public static readonly Histogram<double> AiFirstTokenMs = Meter.CreateHistogram<double>(
        "pinwiz.ai.first_token_ms",
        unit: "ms",
        description: "Time from request to first text-bearing chunk emitted to the client (cache hit replay counts). Tagged with cache_state (hit | miss) and optionally outcome (streamed | refusal). Drives the ADR-0026 §7.1 user-delight revisit triggers: 200ms p95 structured-records latency, 500ms cold-start cache trigger.");

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

    // ── RAG Change Feed worker instrumentation (Phase 4 W3-2 PR-C) ──────
    // The W3-2 RagIngestionWorker (Container App) consumes the
    // `scraped_documents` Cosmos change feed and runs the Application-
    // layer ingestion pipeline per delivered document. The instruments
    // below cover the hosted-service shell (batch lifecycle, dead-letter
    // routing, at-budget short-circuit). Per-document outcome
    // distribution (Indexed / Skipped_* / DeadLettered) is exposed via
    // the pipeline's IngestionOutcome enum return value — surfaced in
    // logs but NOT counted here to avoid double-counting against
    // `pinwiz.rag.indexed_chunks_total` (which the indexer emits at
    // chunk granularity per upserted batch).
    //
    // `pinwiz.rag.changefeed_lease_lag` (ObservableGauge backed by a
    // periodic ChangeFeedEstimator poll) is deferred to a small follow-
    // up PR — the gauge needs a background poll loop + cached-value
    // pattern that's a meaningful design unit on its own; folding it
    // into PR-C would balloon the diff.

    public static readonly Histogram<double> RagChangefeedBatchDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.rag.changefeed_batch_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single Cosmos Change Feed batch handled by the W3-2 RagIngestionWorker — measured at the hosted-service `HandleChangesAsync` boundary so it captures total per-batch time including dead-letter lookups, handler invocations, and upsert calls. Operators chart p50/p95 to detect ingestion slowdowns before they become lease-lag spikes. Tagged with `batch_size_bucket` (`1`, `2-10`, `11-50`, `51+`) so dashboards can attribute latency growth to batch-size shifts vs. per-document slowdown.");

    public static readonly Counter<long> RagChangefeedDeadLetterTotal = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_dead_letter_total",
        unit: "{document}",
        description: "Per-document failures the W3-2 hosted service routed to the `rag_dead_letters` Cosmos container after an exception bubbled out of `ICosmosChangeFeedHandler.HandleAsync`. Tagged with `error_class` (the truncated exception type name — `RequestFailedException`, `InvalidOperationException`, etc.) so dashboards can distinguish AI-Search-side failures from Cosmos-side failures from extractor / chunker bugs. A spike on a single error_class points at a specific upstream regression. Always increments on every dead-letter UPSERT, regardless of whether the AttemptCount has reached MaxFailuresPerDocument — the at-budget short-circuit is observed via `pinwiz.rag.changefeed_short_circuit_total{reason=over_budget}`.");

    public static readonly Counter<long> RagChangefeedShortCircuitTotal = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_short_circuit_total",
        unit: "{document}",
        description: "Per-document Change Feed deliveries the W3-2 hosted service skipped without invoking the handler. Tagged with `reason`: `over_budget` when the dead-letter row's AttemptCount has reached `RagIngestionOptions.MaxFailuresPerDocument` (the structurally-poison-document case — only operator clearing the dead-letter resumes processing); `empty_document_id` when the source-document payload is malformed. Distinguishes operator-actionable signals (over-budget = clear the dead-letter) from data-quality signals (empty id = upstream scraper bug). The pipeline-internal short-circuits (`Skipped_NotInCuratedSubset`, `Skipped_DocumentTypeFiltered`, `Skipped_HashUnchanged`) live below the hosted service and are NOT counted here — they are healthy filtering, not signal-of-trouble.");

    // ── RAG Change Feed lease-lag gauge (W3-2 PR-C follow-up — shipped) ─
    // Backed by `_changefeedLeaseLag`, a process-static cache the hosted
    // service updates from a periodic `ChangeFeedEstimator` poll.
    // ObservableGauge callbacks fire on the metrics-export thread and
    // MUST NOT do I/O — caching is mandatory. Using a static cache means
    // a single process supports a single Change Feed consumer; in
    // production the W3-2 worker is exactly that, and ACA's per-replica
    // process isolation means a multi-replica deploy still reports
    // distinct gauge values per replica via OTel's natural per-instance
    // emission. If a future second consumer ships in the same process,
    // promote the cache to a per-processor `ConcurrentDictionary<string,
    // long>` keyed on processorName and add a `processor_name` tag.

    private static long _changefeedLeaseLag;

    public static readonly ObservableGauge<long> RagChangefeedLeaseLag = Meter.CreateObservableGauge<long>(
        "pinwiz.rag.changefeed_lease_lag",
        observeValue: () => Interlocked.Read(ref _changefeedLeaseLag),
        unit: "{document}",
        description: "Estimated number of source documents the W3-2 RagIngestionWorker is behind the source-container's leading edge — summed across leases via Cosmos's `ChangeFeedEstimator`. Updated by a periodic poll inside `CosmosChangeFeedHostedService.ExecuteAsync` (default 30s cadence; see `CosmosChangeFeedHostedServiceOptions.LeaseLagPollInterval`). Operators alert on persistent non-zero values: a steady positive lag means the worker can't keep up with the change rate — either AI Search throughput is the bottleneck or per-document handler latency has regressed. Pair with `pinwiz.rag.changefeed_batch_duration_ms` p95 to triage.");

    // Package-internal — only the Infrastructure-layer hosted service
    // calls this. Exposed via the parent class rather than a setter on
    // the gauge field so the static-cache invariant stays documented in
    // one place.
    public static void RecordChangefeedLeaseLag(long lag) =>
        Interlocked.Exchange(ref _changefeedLeaseLag, lag);

    // ── RAG Change Feed reconcile-on-startup instruments (W3-2) ─────────
    // Emitted by the W3-2 reconciler when
    // `RagIngestionOptions.ReconcileOnStartup=true`. The pass inspects
    // a recency-biased sample of `rag_index_state` rows and verifies
    // each has matching chunks in AI Search. Operators alert on drift_*
    // counters trending non-zero across deploys — that's a signal that
    // some Phase 1 → AI Search writes are being lost.
    //
    // Per-instance cardinality: one process invokes the reconciler at
    // most once per startup, so these counters/histograms see a small
    // number of observations per worker lifetime.

    public static readonly Counter<long> RagChangefeedReconcileStarted = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_reconcile_started",
        unit: "{run}",
        description: "Reconcile-on-startup invocations the W3-2 hosted service began. Increments once per worker boot when `RagIngestionOptions.ReconcileOnStartup=true` (and zero times otherwise). Pair with `pinwiz.rag.changefeed_reconcile_duration_ms` to chart reconcile cost over time.");

    public static readonly Histogram<double> RagChangefeedReconcileDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.rag.changefeed_reconcile_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single reconcile-on-startup pass — Cosmos sampling query + per-document AI Search filter calls + result aggregation. Measured at the reconciler's outer boundary (caller-visible latency). Useful for capacity planning at Phase 4.5 corpus scaling: if reconcile p95 grows past `RagIngestionOptions.ReconcileSampleSize / 50` × 1s, the per-document AI Search verify cost is the bottleneck and the sample size should drop.");

    public static readonly Counter<long> RagChangefeedReconcileSampled = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_reconcile_sampled_total",
        unit: "{document}",
        description: "Documents the reconcile pass actually inspected (may be less than `RagIngestionOptions.ReconcileSampleSize` if the `rag_index_state` container has fewer rows). Pair with `pinwiz.rag.changefeed_reconcile_drift_total` to compute drift rate (drift / sampled).");

    public static readonly Counter<long> RagChangefeedReconcileDrift = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_reconcile_drift_total",
        unit: "{document}",
        description: "Documents where the reconcile pass detected drift between `rag_index_state` and AI Search. Tagged with `drift_type`: `missing` (AI Search has zero chunks for the document_id — full write loss); `count_mismatch` (AI Search has a different chunk count than recorded — partial write loss). A non-zero rate over multiple deploys is the canonical alert: the indexer isn't durably persisting every chunk, and the gap won't self-heal without an operator-driven re-ingest.");

    // ── Cosmos repository operations (ADR-0025 § 8) ──────────────────────
    // Emitted at the boundary of every `IRepository<T>` SDK call inside
    // `CosmosRepository<T>` (and from `MachineRepository.QueryByTitleAsync`
    // until PR 5 replaces it with a point-read against
    // `machine_title_lookups`). Both instruments carry the same two
    // tags: `container` (the Cosmos container name) + `operation`
    // (`read` | `query` | `upsert` | `delete`).
    //
    // Architectural note: ADR-0025 § 8 originally framed this emission
    // as a `MeteredCosmosRepository<T>` decorator (lower coupling).
    // PR 4 deviated to inline emission inside `CosmosRepository<T>`
    // because `IRepository<T>` is Cosmos-agnostic and does NOT surface
    // `ResponseMessage.RequestCharge` — a decorator over that interface
    // could capture wall-clock duration but not RU. The inline-helper
    // pattern (`ExecuteWithMetricsAsync` in `CosmosRepository<T>`)
    // captures both at the actual SDK boundary, with the helper exposed
    // `protected` so concrete repositories with specialized methods
    // (e.g. `MachineRepository.QueryByTitleAsync`) can wrap their own
    // SDK calls without re-implementing the boundary capture.

    public static readonly Histogram<double> CosmosRuCharge = Meter.CreateHistogram<double>(
        "pinwiz.cosmos.ru_charge",
        unit: "{ru}",
        description: "Cosmos request units consumed by a single SDK call from `CosmosRepository<T>` (or a concrete subclass's specialized method). Tagged with `container` (the Cosmos container name) + `operation` (`read` | `query` | `upsert` | `delete`). For streaming queries, one observation is recorded per page from `iterator.ReadNextAsync()` so heavy multi-page queries don't get hidden inside an aggregate. Failed calls (any `CosmosException` other than 404 — 404 is normal flow for `GetByIdAsync`/`DeleteAsync`) emit the SDK-reported `CosmosException.RequestCharge` so RU spent on a failed operation is still surfaced. Drives the §7.1 user-delight RU-cost-dominance revisit trigger (RU per Wizard answer).");

    public static readonly Histogram<double> CosmosQueryDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.cosmos.query_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single Cosmos SDK call from `CosmosRepository<T>` (or a concrete subclass's specialized method). Same tags as `pinwiz.cosmos.ru_charge`. For streaming queries, one observation per page so a query that paginates through 10 pages emits 10 samples. Drives the §7.1 user-delight 200ms p95 trigger on the `getMachineByTitle` path; PR 5 of the Cosmos delight track is validated against this instrument's pre/post p95 distribution.");

    // ── Activity (trace) names ───────────────────────────────────────────

    public const string OpdbSyncActivity = "pinwiz.opdb.sync";
    public const string PinballMapFetchActivity = "pinwiz.pinballmap.fetch";
    public const string AiRouterActivity = "pinwiz.ai.router";
    public const string EvalRunActivity = "pinwiz.eval.run";
}
