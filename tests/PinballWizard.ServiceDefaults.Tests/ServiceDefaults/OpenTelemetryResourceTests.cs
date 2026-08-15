using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Reflection;
using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

/// <summary>
/// Guards the OTel resource configuration added by GitHub #870.
///
/// Before #870, <see cref="Extensions.ConfigureOpenTelemetry{TBuilder}"/> made
/// no <c>ConfigureResource</c> call. The OTel SDK therefore fell back to its default
/// <c>service.name = "unknown_service:&lt;processname&gt;"</c> — in production:
/// <c>"unknown_service:dotnet"</c> for every host (API, Web, RAG indexer, 20 CLI
/// jobs). Azure Monitor maps <c>service.name</c> to <c>AppRoleName</c>, so all
/// four host types collapsed into one undifferentiated series and workbook tiles
/// scoped to <c>"pinwiz-api"</c> returned empty.
///
/// The fix calls:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .ConfigureResource(r => r.AddService(
///         serviceName: serviceName,
///         serviceVersion: serviceVersion))
/// </code>
/// where <c>serviceName</c> comes from <c>PINWIZ_SERVICE_NAME</c> configuration when
/// present, and falls back to
/// <c>builder.Environment.ApplicationName</c> for long-running hosts
/// (API = "PinballWizard.Api", Web = "PinballWizard.Web",
/// RAG indexer = "PinballWizard.RagIngestionWorker").
///
/// <b>NOTHING SETS <c>PINWIZ_SERVICE_NAME</c> TODAY.</b> The Bicep change that injects
/// it per ACA job is tracked as #875 and is not in this change. Until it ships, all 20
/// scheduled CLI jobs share ApplicationName "PinballWizard.Cli" and remain mutually
/// indistinguishable — better than "unknown_service:dotnet", but NOT yet per-job
/// attribution. The override tests below use in-memory configuration precisely because
/// no deployed environment supplies the variable yet.
///
/// <b>Verification approach:</b> The OTel SDK keeps the provider's <c>Resource</c>
/// as an internal property on <c>TracerProviderSdk</c> / <c>MeterProviderSdk</c>
/// (confirmed via reflection against OpenTelemetry 1.17.0, the version pinned in
/// Directory.Packages.props). Tests access it via reflection, which couples to an
/// SDK implementation detail, but this is the only way to observe the resource
/// without a live Azure Monitor export. The coupling is deliberate, documented here,
/// and the accessor is extracted into a single helper
/// (<see cref="GetServiceNameFromProvider"/>) so a future SDK version that publishes
/// this API requires only a one-line change in that helper.
/// </summary>
public sealed class OpenTelemetryResourceTests
{
    // ─────────────────────────────────────────────────────────────────────
    // service.name = ApplicationName when PINWIZ_SERVICE_NAME is not set
    // (covers: API "PinballWizard.Api", Web "PinballWizard.Web",
    //  RagIngestionWorker "PinballWizard.RagIngestionWorker")
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigureOpenTelemetry_WithoutServiceNameConfig_SetsServiceNameFromApplicationName()
    {
        var builder = Host.CreateApplicationBuilder();
        // PINWIZ_SERVICE_NAME intentionally absent — simulates every long-running host.
        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            var serviceName = GetServiceNameFromProvider(tracerProvider);

            // Before #870: service.name was "unknown_service:dotnet" (the process-name fallback).
            // After the fix: equals builder.Environment.ApplicationName — the entry-point
            // assembly name, e.g. "PinballWizard.Api" in production; the xUnit runner name
            // in the test harness. Either way it is NOT the "unknown_service" sentinel.
            Assert.Equal(builder.Environment.ApplicationName, serviceName);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // service.name = PINWIZ_SERVICE_NAME override when present
    // (covers: scheduled CLI jobs, each with a distinct ACA job resource name
    // injected by Bicep — see commit body for the required Bicep change)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigureOpenTelemetry_WithPinwizServiceNameConfig_UsesConfigAsServiceName()
    {
        const string expectedServiceName = "pinwiz-job-linker-abcde";

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Simulates what Bicep will inject into each CLI job's container env block.
            ["PINWIZ_SERVICE_NAME"] = expectedServiceName,
        });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            var serviceName = GetServiceNameFromProvider(tracerProvider);

            Assert.Equal(expectedServiceName, serviceName);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // An empty / whitespace override must fall back, not blow up startup.
    // ?? only guards null, so a variable set-but-empty slips straight through
    // to AddService. This code runs during startup for EVERY host, so an
    // operator typo must not be able to take the process down.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConfigureOpenTelemetry_WithBlankServiceNameConfig_FallsBackToApplicationName(string blank)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PINWIZ_SERVICE_NAME"] = blank,
        });
        var expectedServiceName = builder.Environment.ApplicationName;

        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();

            Assert.Equal(expectedServiceName, GetServiceNameFromProvider(tracerProvider));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // service.name must never be the OTel SDK default "unknown_service:<…>"
    // Direct regression guard for the #870 failure observable in the portal.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigureOpenTelemetry_ServiceName_IsNeverUnknownServiceDefault()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            var serviceName = GetServiceNameFromProvider(tracerProvider);

            // "unknown_service" is the OTel SDK sentinel when no ConfigureResource
            // call has been made. Any presence of this substring means #870 has regressed.
            Assert.DoesNotContain(
                "unknown_service",
                serviceName,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // MeterProvider must carry the same service.name as TracerProvider.
    // A common misconfiguration applies ConfigureResource to only one signal's
    // pipeline; this test proves the resource is shared across both.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigureOpenTelemetry_MeterProviderResource_MatchesTracerProviderResource()
    {
        const string expectedServiceName = "pinwiz-job-opdb-zzzzz";

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PINWIZ_SERVICE_NAME"] = expectedServiceName,
        });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            var meterProvider = host.Services.GetRequiredService<MeterProvider>();

            var tracerServiceName = GetServiceNameFromProvider(tracerProvider);
            var meterServiceName = GetServiceNameFromProvider(meterProvider);

            Assert.Equal(expectedServiceName, tracerServiceName);
            Assert.Equal(expectedServiceName, meterServiceName);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the <c>service.name</c> attribute from the provider's Resource via
    /// reflection. The OTel SDK (1.17.0) keeps <c>Resource</c> as an internal
    /// property on the concrete <c>TracerProviderSdk</c> / <c>MeterProviderSdk</c>
    /// implementation types — there is no public API for this in 1.17.0. The
    /// coupling is documented in the class-level remarks; a future SDK version that
    /// makes this public needs only this method updated.
    /// </summary>
    private static string GetServiceNameFromProvider(object provider)
    {
        // Each step throws rather than returning null. This is deliberate: if a future
        // OTel version renames or removes the internal Resource property, the reflection
        // silently yields null — and a null flowing into
        // Assert.DoesNotContain("unknown_service", ...) would PASS, turning the #870
        // regression guard into a permanent no-op. A test that quietly stops testing is
        // worse than no test, so the accessor fails loudly at the point the coupling
        // breaks, naming what to fix.
        var resourceProp = provider.GetType()
            .GetProperty("Resource", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"OTel SDK coupling broken: no internal 'Resource' property on {provider.GetType().FullName}. " +
                "The SDK version changed — update GetServiceNameFromProvider (see class remarks).");

        var resource = resourceProp.GetValue(provider) as Resource
            ?? throw new InvalidOperationException(
                $"OTel SDK coupling broken: 'Resource' on {provider.GetType().FullName} was not a Resource.");

        var serviceName = resource.Attributes
            .FirstOrDefault(kv => kv.Key == "service.name")
            .Value?.ToString();

        return serviceName ?? throw new InvalidOperationException(
            "No 'service.name' attribute on the provider resource. Either ConfigureResource " +
            "was not applied (the #870 regression) or the attribute key changed.");
    }
}
