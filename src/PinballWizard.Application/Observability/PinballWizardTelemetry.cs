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

    // ── Activity (trace) names ───────────────────────────────────────────

    public const string OpdbSyncActivity = "pinwiz.opdb.sync";
    public const string PinballMapFetchActivity = "pinwiz.pinballmap.fetch";
    public const string AiRouterActivity = "pinwiz.ai.router";
}
