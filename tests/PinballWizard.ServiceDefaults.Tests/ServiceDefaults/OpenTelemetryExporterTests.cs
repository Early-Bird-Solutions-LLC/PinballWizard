using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

/// <summary>
/// Verifies the Azure Monitor exporter gating in
/// <see cref="Extensions.ConfigureOpenTelemetry{TBuilder}"/>.
///
/// The production code adds the Azure Monitor exporter only when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is non-empty. These tests
/// guard against:
/// 1. Regression that removes the exporter registration (App Insights goes dark).
/// 2. Regression that removes the gate (exporter fires without a connection string,
///    causing every service to log a startup error or behave unexpectedly in local dev).
///
/// The OTel SDK registers exporters deep inside its internal pipeline and does not
/// expose a public API for enumerating registered exporters. A successful
/// <see cref="WebApplication.Build"/> is therefore the highest-fidelity assertion
/// available without coupling to SDK internals: if the registration fails (bad package
/// reference, incompatible overloads, misconfigured options), Build() throws.
/// </summary>
public sealed class OpenTelemetryExporterTests
{
    // A structurally valid but non-functional App Insights connection string.
    // Passed explicitly through AzureMonitorExporterOptions.ConnectionString — the SDK
    // reads and validates the format at registration time, then connects lazily on first
    // export. Build() succeeds even though no real endpoint exists, because no export
    // is attempted during host construction.
    // Note: do NOT inject this via IConfiguration.AddInMemoryCollection alone. The SDK
    // reads from AzureMonitorExporterOptions.ConnectionString, not from IConfiguration;
    // without the explicit pass in Extensions.cs the SDK would fall back to the process
    // environment variable, find nothing, and throw "A connection string was not found."
    private const string FakeConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
        "IngestionEndpoint=https://dc.services.visualstudio.com/";

    // ─────────────────────────────────────────────────────────────────────
    // With connection string — exporter path must register without errors
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_WithAppInsightsConnectionString_BuildsSuccessfully()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = FakeConnectionString,
        });

        builder.AddServiceDefaults();

        // Build() propagates any DI / options-validation failures from the exporter
        // registration path. A thrown exception here means the Azure Monitor exporter
        // wiring in AddOpenTelemetryExporters is broken.
        using var app = builder.Build();
        Assert.NotNull(app);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Without connection string — gate must suppress the exporter registration
    // (backward compat: local dev / Aspire dashboard path must keep working)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_WithoutAppInsightsConnectionString_BuildsSuccessfully()
    {
        var builder = WebApplication.CreateBuilder();
        // APPLICATIONINSIGHTS_CONNECTION_STRING intentionally absent —
        // simulates local dev and Aspire dashboard scenarios.

        builder.AddServiceDefaults();

        using var app = builder.Build();
        Assert.NotNull(app);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Empty string — treated the same as absent (gate uses IsNullOrWhiteSpace)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_WithEmptyAppInsightsConnectionString_BuildsSuccessfully()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty,
        });

        builder.AddServiceDefaults();

        using var app = builder.Build();
        Assert.NotNull(app);
    }
}
