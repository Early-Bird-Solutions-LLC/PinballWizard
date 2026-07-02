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

    public static readonly Counter<long> AiCacheBypassMultiturn = Meter.CreateCounter<long>(
        "pinwiz.ai.cache.bypass_multiturn",
        unit: "{question}",
        description: "Multi-turn asks that bypassed the semantic cache entirely (no read, no write) because the cache key has no history component — a follow-up's meaning depends on its conversation. Watch this against cache.hits/misses to track the cost impact of uncacheable multi-turn traffic (ADR-0015 amendment, 2026-06-11).");

    public static readonly Counter<long> AiCitationsInherited = Meter.CreateCounter<long>(
        "pinwiz.ai.citations.inherited_total",
        unit: "{citation}",
        description: "Citations carried forward from a prior conversation turn because the current turn answered from conversation context without firing a retrieval tool. A high ratio of inherited-to-extracted suggests follow-ups rarely re-ground — expected for clarifying questions, worth investigating if it dominates.");

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
        description: "User-questions where the Wizard routed to a heavy-tier sub-agent (gpt-4.1) after the initial light-tier (gpt-4o) result fell below the confidence threshold (per ADR-0015).");

    public static readonly Histogram<double> AiDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.ai.duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a single user-question round-trip through IAiRouter (cache lookup + Foundry agent invocation + post-process). Complements per-call gen_ai.* durations from auto-emitted spans.");

    public static readonly Counter<long> AiCitationsExtracted = Meter.CreateCounter<long>(
        "pinwiz.ai.citations.extracted_total",
        unit: "{citation}",
        description: "Citations attached to a Wizard answer. Tagged with source (tool_trace | regex_legacy) per ADR-0022 — during the Phase 4 cutover both extractors run; tool_trace is the primary and regex_legacy runs in parallel for behavioral comparison. The relative counts surface drift before the H3 eval baseline rerun.");

    public static readonly Counter<long> AiInlineMarkerTotal = Meter.CreateCounter<long>(
        "pinwiz.ai.inline_marker_total",
        unit: "{marker}",
        description: "Inline [[cite:k]] tokens the model emitted in an answer, before reconciliation.");

    public static readonly Counter<long> AiInlineMarkerRendered = Meter.CreateCounter<long>(
        "pinwiz.ai.inline_marker_rendered_total",
        unit: "{marker}",
        description: "Inline citation markers that reconciled to a real citation and were rewritten to [[cite:N]].");

    public static readonly Counter<long> AiInlineMarkerDropped = Meter.CreateCounter<long>(
        "pinwiz.ai.inline_marker_dropped_total",
        unit: "{marker}",
        description: "Inline [[cite:k]] tokens dropped because no structural citation matched (tagged with reason). OBS-01: degrade visibly.");

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

    // ── AI Search unavailable degradation counter (ADR-0026 § 9 PR-D2) ──
    // Emitted by SearchCorpusTool on any typed catch arm that suppresses a
    // retriever transport failure and returns an empty result. Tags reason
    // so dashboards can distinguish timeout vs. auth failure vs. 5xx.
    public static readonly Counter<long> AiSearchUnavailable = Meter.CreateCounter<long>(
        "pinwiz.ai.search_unavailable_total",
        unit: "{call}",
        description: "SearchCorpusTool calls where the retriever threw and the tool returned an empty result, tagged by reason (timeout | http_5xx | auth_failure | other). Complements pinwiz.ai.tool_errors_total with finer failure-type attribution. Drives the PR-D2 SearchUnavailable degradation-context mark so WizardAnswer.Degradation surfaces to the frontend.");

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

    // Tokens sent to the embedding API across all `IChunkEmbedder.EmbedBatchAsync`
    // calls. Sourced from the SDK's `EmbeddingTokenUsage.InputTokenCount` so it
    // reflects actual billed tokens, not an estimate. Tagged with `call_site`
    // (`backfill` | `changefeed` | `query`) so dashboards split indexing cost
    // from query-time cost. Drives the TPM-ceiling decision: if peak observed
    // tokens/minute during a full rebuild approaches the 250k ceiling, raise the
    // deployment capacity (free — Standard is pay-per-token). A sustained rate
    // near the ceiling means 429s are likely; headroom of ~30% is the target.
    public static readonly Counter<long> RagEmbeddingTokensTotal = Meter.CreateCounter<long>(
        "pinwiz.rag.embedding_tokens_total",
        unit: "{token}",
        description: "Input tokens sent to the embedding API per EmbedBatchAsync call. Sourced from SDK Usage.InputTokenCount (actual billed tokens). Tagged with call_site (backfill | changefeed | query). Use to measure peak tokens/minute during rebuilds vs. the deployed TPM ceiling, and to compute per-rebuild embedding cost at $0.13/1M tokens.");

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

    public static readonly Counter<long> RagIngestionTypeFiltered = Meter.CreateCounter<long>(
        "pinwiz.rag.ingestion_type_filtered_total",
        unit: "{document}",
        description: "Documents skipped before download because their document_type is not in the RAG accepted-types set. Tagged with document_type. A persistent nonzero rate for a type you EXPECT to ingest means a classification or accept-list gap — the silent-drop class that hid the Domain-2 gameplay gap.");

    public static readonly Counter<long> RagChangefeedDeadLetterTotal = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_dead_letter_total",
        unit: "{document}",
        description: "Per-document failures the W3-2 hosted service routed to the `rag_dead_letters` Cosmos container after an exception bubbled out of `ICosmosChangeFeedHandler.HandleAsync`. Tagged with `error_class` (the truncated exception type name — `RequestFailedException`, `InvalidOperationException`, etc.) so dashboards can distinguish AI-Search-side failures from Cosmos-side failures from extractor / chunker bugs. A spike on a single error_class points at a specific upstream regression. Always increments on every dead-letter UPSERT, regardless of whether the AttemptCount has reached MaxFailuresPerDocument — the at-budget short-circuit is observed via `pinwiz.rag.changefeed_short_circuit_total{reason=over_budget}`.");

    public static readonly Counter<long> RagChangefeedShortCircuitTotal = Meter.CreateCounter<long>(
        "pinwiz.rag.changefeed_short_circuit_total",
        unit: "{document}",
        description: "Per-document Change Feed deliveries the W3-2 hosted service skipped without invoking the handler. Tagged with `reason`: `over_budget` when the dead-letter row's AttemptCount has reached `RagIngestionOptions.MaxFailuresPerDocument` (the structurally-poison-document case — only operator clearing the dead-letter resumes processing); `empty_document_id` when the source-document payload is malformed. Distinguishes operator-actionable signals (over-budget = clear the dead-letter) from data-quality signals (empty id = upstream scraper bug). The pipeline-internal short-circuits (`Skipped_DocumentTypeFiltered`, `Skipped_HashUnchanged`) live below the hosted service and are NOT counted here — they are healthy filtering, not signal-of-trouble.");

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

    // ── Cosmos deserialization failure counter (invariant #17 / OBS-01) ──────
    // Incremented by `CosmosMetricsHelper` whenever `System.Text.Json`
    // throws `JsonException` inside a `ReadItemAsync<T>` call — the Cosmos
    // SDK delegates deserialization to `SystemTextJsonCosmosSerializer.FromStream`,
    // so a stored document with the wrong JSON shape (e.g. `matchTokens` written
    // as a flat array instead of `List<List<string>>`) surfaces here.
    //
    // A non-zero rate signals a corrupt stored document that must be remediated
    // (fix the write path, then re-upsert the document). The metric + Error log
    // together satisfy invariant #17: degrade visibly, never fabricate success.
    //
    // Tags:
    //   container  — the Cosmos container name (e.g. `machine_title_lookups`)
    //   operation  — the SDK operation that failed (`read` | `query` | ...)
    public static readonly Counter<long> CosmosDeserializationFailed = Meter.CreateCounter<long>(
        "pinwiz.cosmos.deser_failed_total",
        unit: "{failure}",
        description: "Cosmos point-reads or query pages where System.Text.Json threw JsonException during deserialization (corrupt or schema-mismatched stored document). Tagged with container + operation. A non-zero rate requires operator action: identify the corrupt document from the Error log and re-upsert with the correct shape (invariant #17 / OBS-01).");

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

    // ── Wizard stream fallback counter (invariant #17 operational signal) ───
    //
    // Incremented by WizardAnswerStream each time the primary streaming path
    // throws and the component attempts the whole-response fallback. A
    // non-zero rate signals that the streaming path is failing even though
    // users may still receive an answer via the fallback. The fallback
    // counter increments on ATTEMPT, not on success — pair with
    // wizard.stream.fallback.failed (logged at Error) to see the fraction
    // that also failed the recovery path.
    //
    // No tags: single code path (stream-error catch, one component instance
    // per circuit). If A/B variants ship later, add a `variant` tag then.
    public static readonly Counter<long> WizardStreamFallbackAttempted = Meter.CreateCounter<long>(
        "wizard.stream.fallback.attempted",
        unit: "{attempt}",
        description: "WizardAnswerStream attempts to recover from a stream error via the whole-response fallback path. Non-zero rate is an operational signal that streaming is degraded even if end-users receive an answer. Pair with the wizard.stream.fallback.failed Error log to compute fallback success rate (invariant #17).");

    // ── Landing fallback counter (invariant #17 operational signal) ─────────
    //
    // Incremented by Index.razor each time the interactive-mode initialization
    // receives null from IWizardLandingClient (endpoint unreachable, non-2xx,
    // or transport error). The page continues to serve HTTP 200 with compiled-in
    // fallback content — outer health checks and uptime monitors will not see this
    // outage; this counter is the only operational signal that the landing endpoint
    // is unhealthy even though end-users are still being served a rendered page.
    //
    // A sustained non-zero rate signals: landing endpoint is down but web frontend
    // appears healthy from the outside. Alert on p5m sum > 0 in Prod.
    //
    // No tags — the call site is a single code path (index page, interactive circuit);
    // cardinality stays at 1. If a future A/B variant needs per-variant attribution,
    // add a `variant` tag at that point rather than pre-optimising for a case that
    // doesn't exist yet.
    public static readonly Counter<long> LandingFallbackTotal = Meter.CreateCounter<long>(
        "pinwiz.web.landing_fallback_total",
        unit: "{render}",
        description: "Interactive-mode renders where IWizardLandingClient returned null (endpoint unreachable or non-2xx) and the landing page fell back to the compiled-in static seed questions and featured machines. A sustained non-zero rate is an operational signal that the landing endpoint is unhealthy even though the page continues to serve HTTP 200s (invariant #17).");

    // ── Scraper politeness fallback counter (invariant #17 operational signal)
    //
    // Incremented by IngestionSourcePolitenessResolver each time the Cosmos
    // repository throws during initialization and the resolver falls back to
    // global defaults for every host. A non-zero rate signals that per-source
    // politeness overrides are not being applied — all scraping proceeds at
    // the global default rate, which may be more aggressive than configured
    // for specific sites.
    //
    // No tags: single code path (one resolver instance, one init attempt).
    public static readonly Counter<long> ScraperPolitenessFallbackActive = Meter.CreateCounter<long>(
        "pinwiz.scraper.politeness_fallback_active",
        unit: "{occurrence}",
        description: "IngestionSourcePolitenessResolver fell back to global politeness defaults because the Cosmos repository threw during initialization. All hosts will be scraped at the global default rate; per-source overrides are not applied (invariant #17).");
    // ── Storefront JSON-LD missing degradation counter (invariant #17) ──────
    //
    // Incremented by the three storefront extractors (JJP, BoF, Multimorphic)
    // each time JsonLdProductParser.FindFirstProduct returns null and the
    // extractor falls back to Open Graph / H1 signals. When JSON-LD is absent
    // the extractor produces a name-only GameRecord with empty Editions and no
    // price/status — structured fields are silently missing without this signal.
    //
    // BoF and Multimorphic sites have dropped JSON-LD; a non-zero rate on those
    // sources during normal runs means the scraper is degraded and the
    // structured-field pipeline is hollow. A non-zero rate on JJP is unexpected
    // and likely signals a Shopify theme regression.
    //
    // Tags:
    //   source — the scraper/source name matching ISourceScraper.Name
    //              (JJP | BoF | Multimorphic)
    //   url    — the product page URL where JSON-LD was absent
    public static readonly Counter<long> ScraperJsonLdMissing = Meter.CreateCounter<long>(
        "pinwiz.scraper.jsonld_missing_total",
        unit: "{page}",
        description: "Storefront product page where JsonLdProductParser.FindFirstProduct returned null and the extractor fell back to Open Graph / H1. Structured fields (editions, price, status) will be absent from the resulting GameRecord. Tagged with source (JJP | BoF | Multimorphic) and url. Non-zero rate on BoF/Multimorphic indicates those sites have dropped JSON-LD; non-zero on JJP signals an unexpected Shopify theme regression. Paired with a LogWarning at the point of degradation (invariant #17 / OBS-01).");


    // ── Community-resource load failure counter (invariant #17 / OBS-01) ────
    //
    // Incremented by RefusalRecoveryService whenever ICommunityResourceLoader
    // throws during BuildRecoveryAsync. When this fires, the refusal panel
    // renders with no community CTAs — a visible symptom that looks like
    // "the file resolved but was empty" from the user's perspective, but is
    // actually an infrastructure failure. Without this counter the failure is
    // silent: the catch block in BuildRecoveryAsync returns null (best-effort
    // posture so primary refusals are never blocked) and the only signal is a
    // single Error log entry.
    //
    // A non-zero rate in production means community routing is degraded.
    // Alert: any increment in a 5-minute window warrants investigation — the
    // community_resources.v1.json seed is bundled in the container image and
    // should never be unresolvable in prod. A local non-zero rate signals a
    // dev environment without the seed file in the expected search path
    // (SeedPathResolver resolves via AppContext.BaseDirectory walk-up —
    // verify the file is reachable from the Api's bin output tree).
    //
    // Tags:
    //   reason — short exception class name (FileNotFoundException | InvalidOperationException | other)
    public static readonly Counter<long> AiCommunityResourcesLoadErrors = Meter.CreateCounter<long>(
        "pinwiz.ai.community_resources_load_errors_total",
        unit: "{failure}",
        description: "RefusalRecoveryService calls where ICommunityResourceLoader threw during BuildRecoveryAsync. When non-zero, community routing CTAs are absent from refusal panels. Tagged with reason (FileNotFoundException | InvalidOperationException | other). A non-zero prod rate means the community_resources.v1.json seed is unresolvable in the container — check SeedPathResolver and Dockerfile COPY (invariant #17 / OBS-01).");

    // Related-machines lookup failures during refusal recovery, metered
    // SEPARATELY from community-resource failures so the two independent
    // enrichments are observable on their own. The cross-partition machine-title
    // query (QueryByTitleAsync) and the community-resource load now degrade
    // independently — a failure here does NOT drop community CTAs, and it is no
    // longer mislabeled as a community-resources error.
    //
    // Tags:
    //   reason — short exception class name (FileNotFoundException | InvalidOperationException | other)
    public static readonly Counter<long> AiRelatedMachinesLookupErrors = Meter.CreateCounter<long>(
        "pinwiz.ai.related_machines_lookup_errors_total",
        unit: "{failure}",
        description: "RefusalRecoveryService calls where the related-machines lookup (IMachineRepository.QueryByTitleAsync, a cross-partition machine-title query) threw during BuildRecoveryAsync. When non-zero, related-machine suggestions are absent from refusal panels; community routing CTAs are UNAFFECTED (the two enrichments degrade independently). Tagged with reason. A non-zero rate means the cross-partition machine-title query is failing — check the machines container + Cosmos read path (invariant #17 / OBS-01).");

    // ── Document reclassification instrumentation (--reclassify-documents) ──
    // Emitted by DocumentReclassifier.RunAsync for the CLI maintenance verb
    // that re-runs ClassifyDocumentType over stored scraped_documents_raw
    // records and writes back any changed document_type.
    //
    // pinwiz.reclassify.scanned — every record streamed (regardless of outcome).
    // pinwiz.reclassify.changed — records whose classification changed and were
    //   written back. Tagged with old_type and new_type so dashboards can see
    //   which transitions occurred (e.g. Other → Rulesheet).
    // pinwiz.reclassify.unchanged — records whose classification was already
    //   correct; no write issued (idempotent path).
    // pinwiz.reclassify.failed — per-document failures caught without aborting
    //   the run (invariant #17 degrade-visibly posture).

    public static readonly Counter<long> ReclassifyScanned = Meter.CreateCounter<long>(
        "pinwiz.reclassify.scanned",
        unit: "{document}",
        description: "Documents streamed from scraped_documents_raw by --reclassify-documents. Includes all outcomes (changed, unchanged, failed). Use to track run completeness at corpus scale.");

    public static readonly Counter<long> ReclassifyChanged = Meter.CreateCounter<long>(
        "pinwiz.reclassify.changed",
        unit: "{document}",
        description: "Documents whose document_type changed and were written back by --reclassify-documents. Tagged with old_type and new_type (e.g. Other → Rulesheet). A non-zero count on the Other→Rulesheet transition after PR #507 means Domain 2 activation is working as expected.");

    public static readonly Counter<long> ReclassifyUnchanged = Meter.CreateCounter<long>(
        "pinwiz.reclassify.unchanged",
        unit: "{document}",
        description: "Documents whose ClassifyDocumentType result matched the stored document_type — no write issued. A high unchanged count on re-runs confirms the operation is idempotent.");

    public static readonly Counter<long> ReclassifyFailed = Meter.CreateCounter<long>(
        "pinwiz.reclassify.failed",
        unit: "{document}",
        description: "Per-document errors during --reclassify-documents caught and logged without aborting the run (invariant #17 degrade-visibly). Non-zero rate means some documents were not reclassified; check Error logs for document IDs and exception types.");

    public static readonly Histogram<double> ReclassifyDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.reclassify.duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a complete --reclassify-documents run in milliseconds. Useful for capacity planning at corpus scale.");

    // ── Machine findability index instrumentation (ADR-0049 phase 2a) ──────
    // Emitted by MachineSearchIndexProjector.ProjectAllAsync at the end of
    // each --rebuild-machine-index / --ensure-machine-index + project run.
    // Operators chart MachineIndexProjected to confirm full-corpus coverage
    // (expect ~3 000 at steady state) and MachineIndexProjectionDurationMs
    // to catch regressions as the corpus grows.

    public static readonly Counter<long> MachineIndexProjected = Meter.CreateCounter<long>(
        "pinwiz.machine.index.projected_total",
        unit: "{document}",
        description: "Machine documents successfully upserted into the AI Search machine findability index (ADR-0049) by MachineSearchIndexProjector. Incremented at the end of each projection run by the success count (batch.Count - batchFailed). Pair with pinwiz.machine.index.projection_duration_ms to measure throughput. A value significantly below the known catalog size indicates batch failures — check logs for batch-upsert errors.");

    public static readonly Histogram<double> MachineIndexProjectionDurationMs = Meter.CreateHistogram<double>(
        "pinwiz.machine.index.projection_duration_ms",
        unit: "ms",
        description: "Wall-clock duration of a complete MachineSearchIndexProjector.ProjectAllAsync run in milliseconds — from first Cosmos StreamAllAsync page to final batch flush. Includes Cosmos streaming latency and AI Search batch-upsert latency. Drives capacity planning at corpus scale: if a full rebuild exceeds 5 minutes, consider increasing BatchSize or parallelising the batch-flush loop (ADR-0049 phase 2b follow-up).");

    // ── Activity (trace) names ───────────────────────────────────────────

    public const string OpdbSyncActivity = "pinwiz.opdb.sync";
    public const string PinballMapFetchActivity = "pinwiz.pinballmap.fetch";
    public const string AiRouterActivity = "pinwiz.ai.router";
    public const string EvalRunActivity = "pinwiz.eval.run";
}
