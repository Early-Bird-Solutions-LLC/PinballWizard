using PinballWizard.Application.Observability;
using Xunit;

namespace PinballWizard.Scraper.Tests.Observability;

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
}
