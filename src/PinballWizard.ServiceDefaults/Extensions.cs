using System.Reflection;
using Azure.Core;
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
using OpenTelemetry.Resources;
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
    /// <param name="builder">The host application builder.</param>
    /// <param name="credential">
    /// <para>
    /// The managed-identity credential used to authenticate the Azure Monitor
    /// (App Insights) OTel exporters. Required when
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set and App Insights is
    /// configured with <c>DisableLocalAuth: true</c> (which is the case for
    /// <c>pinwiz-ai-dev</c> and all pinwiz App Insights resources — key-based
    /// ingestion is rejected). Without this credential the exporters cannot
    /// authenticate and telemetry is silently dropped; a startup warning is
    /// logged instead so the failure is visible in the logs.
    /// </para>
    /// <para>
    /// Pass <c>SharedAzureCredential.Instance</c> from
    /// <c>PinballWizard.Infrastructure.Credentials</c> — this is the
    /// process-wide singleton credential that avoids multiple token-cache
    /// contention (issue #362). Never pass <c>new DefaultAzureCredential()</c>
    /// here; that re-creates the defect #362 fixed.
    /// </para>
    /// <para>
    /// Omit (or pass <see langword="null"/>) only in local dev / Aspire
    /// dashboard scenarios where the OTLP exporter handles export instead
    /// of Azure Monitor.
    /// </para>
    /// </param>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, TokenCredential? credential = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry(credential);

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
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, TokenCredential? credential = null)
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

        // Derive the OTel service identity (#870). Without a ConfigureResource call
        // the SDK falls back to "unknown_service:<processname>" — in production:
        // "unknown_service:dotnet" for every host, making all four host types
        // (API, Web, RAG indexer, 20 CLI jobs) collapse into one AppRoleName in
        // Azure Monitor and rendering any tile scoped to a single host empty.
        //
        // PINWIZ_SERVICE_NAME is the per-job override hook. NOTE: as of this commit
        // NOTHING SETS IT — verified against infra/modules/shared.bicep, which injects
        // APPLICATIONINSIGHTS_CONNECTION_STRING, AZURE_CLIENT_ID, Cosmos__*,
        // Scraper__DataPath and Storage__BlobEndpoint, but not this. Until the Bicep
        // change lands (#875), all 20 scheduled CLI jobs share ApplicationName
        // "PinballWizard.Cli" and remain mutually indistinguishable — still a strict
        // improvement on "unknown_service:dotnet", but NOT yet per-job attribution.
        //
        // The Bicep change is one line per job, reusing the jobName each already
        // computes: { name: 'PINWIZ_SERVICE_NAME', value: jobName }. Once deployed,
        // each job reports its own name (e.g. "pinwiz-job-linker-buutj").
        //
        // Long-running hosts (API, Web, RagIngestionWorker) intentionally never need
        // the override — their ApplicationName is already distinct per process.
        // IsNullOrWhiteSpace, not ??. A `??` guards only null, and a variable that is
        // SET BUT BLANK is a realistic operator typo with two distinct failure modes,
        // both verified empirically against OTel 1.17.0:
        //   ""    -> AddService throws ArgumentException while LoggerProviderSdk is
        //            being constructed, which takes down host startup entirely.
        //   "   " -> does NOT throw; service.name becomes "   ", so the host reports a
        //            blank AppRoleName in the portal — the #870 symptom wearing a
        //            different mask, and harder to spot than unknown_service:dotnet.
        // Falling back is right for both: an unusable override should degrade to the
        // process-derived name, never crash the host and never emit a blank identity.
        var configuredServiceName = builder.Configuration["PINWIZ_SERVICE_NAME"];
        var serviceName = string.IsNullOrWhiteSpace(configuredServiceName)
            ? builder.Environment.ApplicationName
            : configuredServiceName;

        // Service version from the entry-point assembly. In CI builds the informational
        // version carries the git SHA suffix (e.g. "1.0.0+abcdef"); in local dev it is
        // whatever the assembly sets. Null is valid and omits the service.version
        // attribute rather than forcing a placeholder.
        var serviceVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        builder.Services.AddOpenTelemetry()
            // ConfigureResource applies to ALL signals (metrics, traces, logs) because
            // it is chained on the shared OpenTelemetryBuilder, not on a per-signal
            // builder. The Azure Monitor exporter reads service.name from this resource
            // and maps it to AppRoleName, so this single call fixes the portal identity
            // for every host type.
            .ConfigureResource(r => r.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion))
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

        builder.AddOpenTelemetryExporters(credential);

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder, TokenCredential? credential)
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
        //
        // CREDENTIAL REQUIREMENT: pinwiz-ai-dev (and all pinwiz App Insights resources)
        // have DisableLocalAuth=true, so instrumentation-key ingestion is rejected. A
        // TokenCredential (the shared UAMI via SharedAzureCredential.Instance) MUST be
        // supplied. Without it the exporter is silently rejected; a startup warning is
        // emitted instead so the failure is not invisible (#840 root cause).
        var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            if (credential is null)
            {
                // No credential supplied — registering the exporter would produce a
                // silent auth failure (rejected by DisableLocalAuth=true). Instead,
                // log a loud warning on host startup so the gap is visible in logs.
                // Repo invariant: "fallbacks must not hide failures."
                builder.Services.AddHostedService(sp =>
                    new TelemetryCredentialWarningService(
                        sp.GetRequiredService<ILogger<TelemetryCredentialWarningService>>()));
            }
            else
            {
                // Pass the connection string explicitly rather than relying on the SDK's
                // env-var autodiscovery. The SDK reads AzureMonitorExporterOptions.ConnectionString
                // at registration time; if it is empty the SDK falls back to the process
                // environment variable, which is correct on ACA but fails under test because
                // IConfiguration.AddInMemoryCollection does not set process environment variables.
                // Explicit wiring is more testable and equally correct in production.
                //
                // Credential: the caller-supplied TokenCredential authenticates all three
                // exporters against the App Insights resource. The UAMI (pinwiz-aca-id-dev,
                // acaIdentity in Bicep) carries the Monitoring Metrics Publisher role on the
                // App Insights resource — the only principal with that grant (#840 fix).
                builder.Services.AddOpenTelemetry()
                    .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(o =>
                    {
                        o.ConnectionString = appInsightsConnectionString;
                        o.Credential = credential;
                    }))
                    .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(o =>
                    {
                        o.ConnectionString = appInsightsConnectionString;
                        o.Credential = credential;
                    }));
                builder.Logging.AddOpenTelemetry(logging =>
                    logging.AddAzureMonitorLogExporter(o =>
                    {
                        o.ConnectionString = appInsightsConnectionString;
                        o.Credential = credential;
                    }));
            }
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

    /// <summary>
    /// Emits a startup warning when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>
    /// is set but no <see cref="TokenCredential"/> was supplied to
    /// <see cref="AddServiceDefaults"/>. The App Insights resource has
    /// <c>DisableLocalAuth=true</c> and rejects key-based ingestion silently;
    /// this service makes the misconfiguration visible in the host's logs.
    /// </summary>
    private sealed class TelemetryCredentialWarningService(ILogger<TelemetryCredentialWarningService> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning(
                "APPLICATIONINSIGHTS_CONNECTION_STRING is set but no TokenCredential was supplied " +
                "to AddServiceDefaults. App Insights has local auth disabled (DisableLocalAuth=true) — " +
                "telemetry will be dropped: App Insights has local auth disabled and no managed " +
                "identity credential was supplied. Pass SharedAzureCredential.Instance (from " +
                "PinballWizard.Infrastructure.Credentials) to AddServiceDefaults(credential: ...).");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
