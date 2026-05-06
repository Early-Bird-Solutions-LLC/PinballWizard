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

    // ── Activity (trace) names ───────────────────────────────────────────

    public const string OpdbSyncActivity = "pinwiz.opdb.sync";
    public const string PinballMapFetchActivity = "pinwiz.pinballmap.fetch";
}
