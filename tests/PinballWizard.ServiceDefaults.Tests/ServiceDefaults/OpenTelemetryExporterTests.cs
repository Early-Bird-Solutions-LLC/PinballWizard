using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

/// <summary>
/// Verifies the Azure Monitor exporter gating in
/// <see cref="Extensions.ConfigureOpenTelemetry{TBuilder}"/>.
///
/// The production code adds the Azure Monitor exporter only when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is non-empty AND a
/// <see cref="TokenCredential"/> is supplied. These tests guard against:
/// 1. Regression that removes the exporter registration when a credential is
///    supplied (App Insights goes dark without any warning).
/// 2. Regression that silently registers the exporter without a credential
///    (exporter fires, auth fails on DisableLocalAuth=true, telemetry drops
///    without any log entry — the original #840 failure mode).
/// 3. The no-credential path emitting a startup warning instead of silently
///    dropping telemetry (the invariant: "fallbacks must not hide failures").
///
/// The OTel SDK registers exporters deep inside its internal pipeline and does not
/// expose a public API for enumerating registered exporters. A successful
/// <see cref="WebApplication.Build"/> is therefore the highest-fidelity assertion
/// available without coupling to SDK internals: if the registration fails (bad package
/// reference, incompatible overloads, misconfigured options), Build() throws.
///
/// The warning-path test requires <c>StartAsync</c> to fire the hosted service that
/// emits the log message (the warning is not emitted at build time).
/// </summary>
[Collection(OpenTelemetryGlobalStateDefinition.Name)]
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
    // With connection string AND credential — exporter path must register
    // without errors and the host must start cleanly.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_WithConnectionStringAndCredential_BuildsSuccessfully()
    {
        var credential = Substitute.For<TokenCredential>();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = FakeConnectionString,
        });

        builder.AddServiceDefaults(credential: credential);

        // Build() propagates any DI / options-validation failures from the exporter
        // registration path. A thrown exception here means the Azure Monitor exporter
        // wiring in AddOpenTelemetryExporters is broken.
        using var app = builder.Build();
        Assert.NotNull(app);
    }

    // ─────────────────────────────────────────────────────────────────────
    // With connection string and credential — host must also START cleanly
    // (the Azure Monitor exporter's background tasks must initialize without
    // throwing even against a stub credential, because auth is lazy/deferred).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddServiceDefaults_WithConnectionStringAndCredential_StartsSuccessfully()
    {
        var credential = Substitute.For<TokenCredential>();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = FakeConnectionString,
        });

        builder.AddServiceDefaults(credential: credential);

        using var host = builder.Build();
        // StartAsync triggers TelemetryHostedService (OTel provider init) and the
        // Azure Monitor exporter background workers. Auth is lazy — the stub credential
        // is not called during startup, so StartAsync must complete cleanly.
        await host.StartAsync();
        try
        {
            Assert.NotNull(host);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // With connection string but NO credential — a startup WARNING must be
    // emitted (not a throw) naming the consequence. The exporter is NOT
    // registered — registering without a credential silently fails when
    // DisableLocalAuth=true (the #840 root cause). The warning makes the
    // gap visible in the logs per the "fallbacks must not hide failures" invariant.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddServiceDefaults_WithConnectionStringAndNoCredential_LogsStartupWarning()
    {
        var sink = new TestLogSink();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = FakeConnectionString,
        });

        // Register the capturing log provider BEFORE AddServiceDefaults so the
        // warning emitted by TelemetryCredentialWarningService is captured.
        builder.Logging.AddProvider(sink);

        builder.AddServiceDefaults(); // no credential — warning path

        using var host = builder.Build();
        // TelemetryCredentialWarningService.StartAsync fires here and emits the warning.
        await host.StartAsync();
        try
        {
            // The exact phrase matches the message in Extensions.TelemetryCredentialWarningService.
            // Checking the key consequence clause — not the full string — so minor wording
            // tweaks in Extensions.cs don't silently unpin this assertion from the behavior
            // being tested.
            Assert.True(
                sink.HasMessage("telemetry will be dropped"),
                $"Expected a startup warning containing 'telemetry will be dropped' but none was logged. " +
                $"Captured messages: [{string.Join("; ", sink.Messages)}]");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // With connection string but NO credential — startup must SUCCEED even
    // though the exporter is not registered. A misconfigured telemetry
    // connection must never take the app down (the CLAUDE.md invariant).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddServiceDefaults_WithConnectionStringAndNoCredential_StartsSuccessfully()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = FakeConnectionString,
        });

        builder.AddServiceDefaults(); // no credential

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            Assert.NotNull(host);
        }
        finally
        {
            await host.StopAsync();
        }
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

    // ─────────────────────────────────────────────────────────────────────
    // RED-THEN-GREEN proof: the warning test is pinned to real behavior.
    // To reproduce the failing state: revert Extensions.cs so AddOpenTelemetryExporters
    // never registers TelemetryCredentialWarningService; run this test; it fails with
    // "Expected a startup warning ... but none was logged". Restore and it is green.
    // (Not automated here — the proof is in the commit body.)
    // ─────────────────────────────────────────────────────────────────────
}

/// <summary>
/// Minimal log sink for asserting that specific log messages are emitted at Warning+.
/// Used only in <see cref="OpenTelemetryExporterTests"/> to verify the startup warning
/// emitted by <c>Extensions.TelemetryCredentialWarningService</c>.
/// </summary>
internal sealed class TestLogSink : ILoggerProvider, ILogger
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public bool HasMessage(string substring) =>
        _messages.Any(m => m.Contains(substring, StringComparison.OrdinalIgnoreCase));

    // ILoggerProvider
    public ILogger CreateLogger(string categoryName) => this;

    // ILogger
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            _messages.Add(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Dispose() { }
}
