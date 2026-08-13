using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace PinballWizard.ServiceDefaults;

/// <summary>
/// Aspire shared-services extensions. Every service that runs under
/// <c>PinballWizard.AppHost</c> calls
/// <see cref="AddServiceDefaults"/> from its host builder so it picks
/// up OpenTelemetry, service discovery, standard resilience, and the
/// liveness / readiness health-check endpoints uniformly.
/// </summary>
/// <remarks>
/// Intentionally minimal v1 — adds only what every Aspire-orchestrated
/// service needs. Scope expands as Phase 2 services come online (auth
/// providers, distributed cache, problem-details handlers etc.). The
/// Neighborli <c>ServiceDefaultsExtensions</c> at
/// <c>c:/projects/Neighborli/src/common/Neighborli.ServiceDefaults/Extensions.cs</c>
/// is the eventual-state reference.
/// </remarks>
public static class Extensions
{
    private static readonly string[] AllowedSchemes = ["https"];

    /// <summary>
    /// Wires the Aspire shared defaults (OTel, service discovery,
    /// resilient HTTP defaults, health checks) into the supplied host
    /// builder. Returns the builder so calls can chain.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // The standard handler's defaults are aimed at typical
            // service-to-service calls (30s total / 10s per attempt).
            // PinballWizard hits two classes of endpoint that exceed
            // those budgets: bulk catalog APIs (OPDB `/api/export`
            // returns ~2.4 MB / ~2,360 records in one response and can
            // take 30s+ when the upstream cache is cold) and Vue.js
            // pages that wait for `networkidle` (Stern's game pages
            // routinely take 15–25s). Bumping the defaults to 120s
            // total / 50s per attempt gives both classes headroom while
            // still bounding hung calls. Per-client overrides remain
            // possible if a future endpoint needs different bounds.
            http.AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(50);
                // Circuit breaker sampling duration must be >= 2 * AttemptTimeout
                // (asserted by HttpStandardResilienceOptions.Validate). Default
                // is 30s and would fail validation when AttemptTimeout > 15s.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
            });
            http.AddServiceDiscovery();
        });

        builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        {
            options.AllowedSchemes = AllowedSchemes;
        });

        return builder;
    }

    /// <summary>
    /// Adds the OpenTelemetry providers (logs / metrics / traces) and
    /// — when the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment
    /// variable is set (Aspire dashboard sets this automatically) —
    /// wires the OTLP exporter that ships them to the dashboard.
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Per ADR-0014 + ADR-0015, enable Foundry's auto-emitted GenAI
        // OTel spans on the Azure.AI.Projects.* activity source. This
        // switch must be set BEFORE any AIProjectClient is constructed,
        // so it fires here at host-builder configuration time. The
        // companion env var AZURE_EXPERIMENTAL_ENABLE_GENAI_TRACING also
        // works; the switch takes precedence per the SDK docs.
        AppContext.SetSwitch("Azure.Experimental.EnableGenAITracing", true);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Project-wide Meter + ActivitySource name. Mirrors the constants
        // on PinballWizard.Application.Observability.PinballWizardTelemetry —
        // duplicated as a literal here (not a typed reference) to avoid a
        // ServiceDefaults → Application project reference, which would
        // invert the layering. See docs/observability.md for the contract.
        const string PinballWizardMeterName = "PinballWizard";
        const string PinballWizardSourceName = "PinballWizard";

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(PinballWizardMeterName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource(PinballWizardSourceName)
                    // Foundry SDK auto-emits spans here when the
                    // EnableGenAITracing switch is on (set above).
                    .AddSource("Azure.AI.Projects.*")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Azure Monitor exporter — metrics, traces, and logs — when the App Insights
        // connection string is present (#840). Applies to ALL host types (CLI/ACA
        // scheduled jobs, API, Web, RagIngestionWorker) because every service that calls
        // AddServiceDefaults() goes through this path. In local dev (Aspire), the OTLP
        // exporter above handles export; this block is a no-op when the variable is absent.
        // The Bicep env blocks in infra/modules/shared.bicep supply the connection string
        // for all ACA resources (container apps + every scheduled-cli-job caller).
        // Note: Bicep env changes take effect only after a stack run
        // (Deploy-SharedResources.ps1) — image-only merges do not apply Bicep (#651).
        var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            // Pass the connection string explicitly rather than relying on the SDK's
            // env-var autodiscovery. The SDK reads AzureMonitorExporterOptions.ConnectionString
            // at registration time; if it is empty the SDK falls back to the process
            // environment variable, which is correct on ACA but fails under test because
            // IConfiguration.AddInMemoryCollection does not set process environment variables.
            // Explicit wiring is more testable and equally correct in production.
            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(
                    o => o.ConnectionString = appInsightsConnectionString))
                .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(
                    o => o.ConnectionString = appInsightsConnectionString));
            builder.Logging.AddOpenTelemetry(logging =>
                logging.AddAzureMonitorLogExporter(o => o.ConnectionString = appInsightsConnectionString));
        }

        return builder;
    }

    /// <summary>
    /// Registers a default <c>self</c> health check tagged
    /// <c>live</c>. Used by the
    /// <see cref="MapDefaultEndpoints"/> mappings to back the
    /// <c>/healthz</c> (readiness) and <c>/alive</c> (liveness)
    /// endpoints that Container Apps health probes hit.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps the default <c>/healthz</c> (readiness — every check) and
    /// <c>/alive</c> (liveness — only checks tagged <c>live</c>)
    /// endpoints. Call from any ASP.NET Core app that uses
    /// <see cref="AddServiceDefaults"/>; calling from a console-host
    /// app is unnecessary.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // AllowAnonymous: health probes must be reachable by ACA infrastructure
        // without auth tokens. The FallbackPolicy in Program.cs requires auth on
        // all undecorated routes; these endpoints need an explicit exemption.
        app.MapHealthChecks("/healthz").AllowAnonymous();

        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
        }).AllowAnonymous();

        return app;
    }
}
