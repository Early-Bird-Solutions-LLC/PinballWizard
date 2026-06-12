using PinballWizard.Application.Observability;
using Xunit;

namespace PinballWizard.Application.Tests.Observability;

// Pins the public surface of the project's Meter and ActivitySource —
// names, instrument names, units, and descriptions. These values are
// part of the operability contract:
//
//   - The names appear in dashboards and alert rules
//   - ServiceDefaults' AddMeter("PinballWizard") + AddSource("PinballWizard")
//     wiring depends on the same string literals
//   - docs/observability.md documents the inventory; this test catches
//     drift when an instrument is renamed or removed without updating
//     the doc + dashboard query
//
// Dashboards are downstream of these names. A rename without coordinated
// dashboard update silently zeroes a chart. This test is the local guard.
public sealed class PinballWizardTelemetryTests
{
    [Fact]
    public void MeterAndActivitySource_HaveStableNames()
    {
        Assert.Equal("PinballWizard", PinballWizardTelemetry.MeterName);
        Assert.Equal("PinballWizard", PinballWizardTelemetry.ActivitySourceName);
        Assert.Equal("PinballWizard", PinballWizardTelemetry.Meter.Name);
        Assert.Equal("PinballWizard", PinballWizardTelemetry.ActivitySource.Name);
    }

    [Fact]
    public void OpdbSyncCounters_HaveExpectedNamesAndUnits()
    {
        Assert.Equal("pinwiz.opdb.sync.fetched", PinballWizardTelemetry.OpdbSyncFetched.Name);
        Assert.Equal("{record}", PinballWizardTelemetry.OpdbSyncFetched.Unit);

        Assert.Equal("pinwiz.opdb.sync.inserted", PinballWizardTelemetry.OpdbSyncInserted.Name);
        Assert.Equal("{machine}", PinballWizardTelemetry.OpdbSyncInserted.Unit);

        Assert.Equal("pinwiz.opdb.sync.updated", PinballWizardTelemetry.OpdbSyncUpdated.Name);
        Assert.Equal("{machine}", PinballWizardTelemetry.OpdbSyncUpdated.Unit);

        Assert.Equal("pinwiz.opdb.sync.skipped", PinballWizardTelemetry.OpdbSyncSkipped.Name);
        Assert.Equal("{record}", PinballWizardTelemetry.OpdbSyncSkipped.Unit);

        Assert.Equal("pinwiz.opdb.sync.failed", PinballWizardTelemetry.OpdbSyncFailed.Name);
        Assert.Equal("{run}", PinballWizardTelemetry.OpdbSyncFailed.Unit);
    }

    [Fact]
    public void OpdbSyncDurationHistogram_HasExpectedNameAndUnit()
    {
        Assert.Equal("pinwiz.opdb.sync.duration_ms", PinballWizardTelemetry.OpdbSyncDurationMs.Name);
        Assert.Equal("ms", PinballWizardTelemetry.OpdbSyncDurationMs.Unit);
    }

    [Fact]
    public void OpdbSyncActivity_HasExpectedName()
    {
        Assert.Equal("pinwiz.opdb.sync", PinballWizardTelemetry.OpdbSyncActivity);
    }

    [Fact]
    public void AllOpdbInstruments_HavePinwizOpdbSyncPrefix()
    {
        // Every OPDB-sync instrument lives under the same namespace, so a
        // dashboard wildcard query like `pinwiz.opdb.sync.*` covers them all.
        var prefix = "pinwiz.opdb.sync.";
        Assert.StartsWith(prefix, PinballWizardTelemetry.OpdbSyncFetched.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.OpdbSyncInserted.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.OpdbSyncUpdated.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.OpdbSyncSkipped.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.OpdbSyncFailed.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.OpdbSyncDurationMs.Name);
    }

    // RAG indexing + retrieval instruments per build-spec § Phase 4 scope
    // item 25 + ADR-0021. Same name/unit pinning posture as the OPDB
    // suite above — drift here zeroes Phase 4 RAG dashboards silently.

    [Fact]
    public void RagIndexingInstruments_HaveExpectedNamesAndUnits()
    {
        Assert.Equal("pinwiz.rag.indexing_duration_ms", PinballWizardTelemetry.RagIndexingDurationMs.Name);
        Assert.Equal("ms", PinballWizardTelemetry.RagIndexingDurationMs.Unit);

        Assert.Equal("pinwiz.rag.indexed_chunks_total", PinballWizardTelemetry.RagIndexedChunks.Name);
        Assert.Equal("{chunk}", PinballWizardTelemetry.RagIndexedChunks.Unit);
    }

    [Fact]
    public void RagRetrievalInstruments_HaveExpectedNamesAndUnits()
    {
        Assert.Equal("pinwiz.rag.retrieval_duration_ms", PinballWizardTelemetry.RagRetrievalDurationMs.Name);
        Assert.Equal("ms", PinballWizardTelemetry.RagRetrievalDurationMs.Unit);

        Assert.Equal("pinwiz.rag.retrieval_score_distribution", PinballWizardTelemetry.RagRetrievalScoreDistribution.Name);
        Assert.Equal("{score}", PinballWizardTelemetry.RagRetrievalScoreDistribution.Unit);
    }

    [Fact]
    public void AllRagInstruments_HavePinwizRagPrefix()
    {
        // Dashboard wildcard query `pinwiz.rag.*` should reach every Phase
        // 4 RAG instrument. Drift on the prefix breaks the dashboard
        // taxonomy — catch it here.
        var prefix = "pinwiz.rag.";
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagIndexingDurationMs.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagIndexedChunks.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagRetrievalDurationMs.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagRetrievalScoreDistribution.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedBatchDurationMs.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedDeadLetterTotal.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedShortCircuitTotal.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedLeaseLag.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileStarted.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileDurationMs.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileSampled.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileDrift.Name);
    }

    // Per-tool latency histogram. Drives the §7.1 architecture-v2 user-
    // delight revisit triggers (200ms p95 structured-records latency,
    // 500ms cold-start for searchCorpus). Same name/unit pinning posture
    // as the OPDB and RAG suites above.

    [Fact]
    public void AiToolDurationMsHistogram_HasExpectedNameAndUnit()
    {
        Assert.Equal("pinwiz.ai.tool_duration_ms", PinballWizardTelemetry.AiToolDurationMs.Name);
        Assert.Equal("ms", PinballWizardTelemetry.AiToolDurationMs.Unit);
    }

    // RAG Change Feed worker instruments per build-spec § Phase 4 W3-2.
    // The hosted-service shell (CosmosChangeFeedHostedService<T>) emits
    // these as part of every batch; see RagChangefeedTelemetryTests for
    // the emission-behavior pinning. Same name/unit pinning posture as
    // the OPDB / RAG / AI tool suites above — drift here zeroes the
    // operator dashboards for the W3-2 worker silently.

    [Fact]
    public void RagChangefeedInstruments_HaveExpectedNamesAndUnits()
    {
        Assert.Equal(
            "pinwiz.rag.changefeed_batch_duration_ms",
            PinballWizardTelemetry.RagChangefeedBatchDurationMs.Name);
        Assert.Equal("ms", PinballWizardTelemetry.RagChangefeedBatchDurationMs.Unit);

        Assert.Equal(
            "pinwiz.rag.changefeed_dead_letter_total",
            PinballWizardTelemetry.RagChangefeedDeadLetterTotal.Name);
        Assert.Equal("{document}", PinballWizardTelemetry.RagChangefeedDeadLetterTotal.Unit);

        Assert.Equal(
            "pinwiz.rag.changefeed_short_circuit_total",
            PinballWizardTelemetry.RagChangefeedShortCircuitTotal.Name);
        Assert.Equal("{document}", PinballWizardTelemetry.RagChangefeedShortCircuitTotal.Unit);
    }

    [Fact]
    public void RagChangefeedInstruments_AllUnderRagPrefix()
    {
        // Dashboard wildcard `pinwiz.rag.changefeed_*` should reach the
        // entire W3-2 worker telemetry surface. Drift on the prefix
        // breaks the dashboard taxonomy — pinned here.
        var prefix = "pinwiz.rag.changefeed_";
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedBatchDurationMs.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedDeadLetterTotal.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedShortCircuitTotal.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedLeaseLag.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileStarted.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileDurationMs.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileSampled.Name);
        Assert.StartsWith(prefix, PinballWizardTelemetry.RagChangefeedReconcileDrift.Name);
    }

    [Fact]
    public void RagChangefeedLeaseLagGauge_HasExpectedNameAndUnit()
    {
        Assert.Equal(
            "pinwiz.rag.changefeed_lease_lag",
            PinballWizardTelemetry.RagChangefeedLeaseLag.Name);
        Assert.Equal("{document}", PinballWizardTelemetry.RagChangefeedLeaseLag.Unit);
    }

    // Reconcile-on-startup instruments per W3-2 follow-up. The
    // reconciler emits these once per worker boot when
    // RagIngestionOptions.ReconcileOnStartup=true. Pinned alongside
    // the other changefeed instruments so a rename trips the test
    // before it silently breaks the operator dashboard.

    [Fact]
    public void RagChangefeedReconcileInstruments_HaveExpectedNamesAndUnits()
    {
        Assert.Equal(
            "pinwiz.rag.changefeed_reconcile_started",
            PinballWizardTelemetry.RagChangefeedReconcileStarted.Name);
        Assert.Equal("{run}", PinballWizardTelemetry.RagChangefeedReconcileStarted.Unit);

        Assert.Equal(
            "pinwiz.rag.changefeed_reconcile_duration_ms",
            PinballWizardTelemetry.RagChangefeedReconcileDurationMs.Name);
        Assert.Equal("ms", PinballWizardTelemetry.RagChangefeedReconcileDurationMs.Unit);

        Assert.Equal(
            "pinwiz.rag.changefeed_reconcile_sampled_total",
            PinballWizardTelemetry.RagChangefeedReconcileSampled.Name);
        Assert.Equal("{document}", PinballWizardTelemetry.RagChangefeedReconcileSampled.Unit);

        Assert.Equal(
            "pinwiz.rag.changefeed_reconcile_drift_total",
            PinballWizardTelemetry.RagChangefeedReconcileDrift.Name);
        Assert.Equal("{document}", PinballWizardTelemetry.RagChangefeedReconcileDrift.Unit);
    }

    [Fact]
    public void LandingFallbackTotalCounter_HasExpectedNameAndUnit()
    {
        // Pins the OTel contract for the landing-fallback visibility counter
        // (issue #366, invariant #17). Dashboard alert relies on this name.
        Assert.Equal("pinwiz.web.landing_fallback_total", PinballWizardTelemetry.LandingFallbackTotal.Name);
        Assert.Equal("{render}", PinballWizardTelemetry.LandingFallbackTotal.Unit);
    }

    [Fact]
    public void RecordChangefeedLeaseLag_UpdatesCachedValueObservedByGauge()
    {
        // The gauge callback reads `Interlocked.Read(ref _changefeedLeaseLag)`;
        // the static `RecordChangefeedLeaseLag(long)` setter is the only
        // way for the hosted service to push a fresh sample. Wired with
        // a MeterListener since `ObservableGauge.GetCurrentValue` isn't
        // public — we trigger a single observation cycle and read the
        // sample.
        const long sentinel = 12345;
        PinballWizardTelemetry.RecordChangefeedLeaseLag(sentinel);

        long? observed = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            observed = value;
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.RagChangefeedLeaseLag);
        listener.RecordObservableInstruments();

        // NotNull first so a regression that fails to wire the gauge at
        // all (callback never fires) reports a clearer "no observation"
        // failure rather than the misleading "0 != 12345" of an Equal
        // assertion against a default-valued nullable.
        Assert.NotNull(observed);
        Assert.Equal(sentinel, observed);

        // Reset to 0 so a sibling test that expects a fresh
        // measurement doesn't see the leftover sentinel from this test.
        PinballWizardTelemetry.RecordChangefeedLeaseLag(0);
    }
}
