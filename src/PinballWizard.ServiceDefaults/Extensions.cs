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
            http.AddStandardResilienceHandler();
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

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
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

        app.MapHealthChecks("/healthz");

        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
        });

        return app;
    }
}
