# 0055 — Azure Monitor OTel exporters wired explicitly, with the connection string passed in code

**Status:** Accepted
**Date:** 2026-08-13

## Context

Every host in this solution builds its telemetry through
`PinballWizard.ServiceDefaults.AddServiceDefaults()`, which configures OpenTelemetry
once for the API, Web, RagIngestionWorker and the CLI (which is what the scheduled ACA
jobs run). Until #846 that configuration registered **only** the OTLP exporter — the one
the Aspire dashboard injects locally via `OTEL_EXPORTER_OTLP_ENDPOINT`.

`docs/observability.md` described the deployed path as the same thing: Container Apps
would "inject the same env var pointing at Application Insights' OTLP endpoint, so the
exporter wiring is unchanged across environments." That was never true of the deployed
system. `OTEL_EXPORTER_OTLP_ENDPOINT` appears nowhere in `infra/modules/shared.bicep`,
so in Azure nothing was exported at all: the counters inventoried in
`docs/observability.md` — including `pinwiz.linker.documents_processed_total` and
`pinwiz.linker.extraction_skipped_total` — incremented into a process that had no
exporter attached. Issue #840 is that gap, found when a linker acceptance run produced
zero rows in `AppMetrics` while `ContainerAppConsoleLogs_CL` showed the job working
normally.

ADR-0014 lists `Azure.Monitor.OpenTelemetry.AspNetCore` among its SDK pins "for App
Insights export post-deployPhase2". That package is an ASP.NET Core distro: it attaches
to the web request pipeline. Two of the four hosts that emit `pinwiz.*` telemetry — the
CLI/job host and the worker — are not ASP.NET Core applications, and the linker job is
precisely the workload whose telemetry was missing. The pin therefore does not cover the
case that motivated this work, and no ADR recorded what the deployed transport actually
is.

## Decision

`AddOpenTelemetryExporters()` in `ServiceDefaults` registers the **Azure Monitor
exporters directly** — `AddAzureMonitorMetricExporter`, `AddAzureMonitorTraceExporter`
and `AddAzureMonitorLogExporter` from `Azure.Monitor.OpenTelemetry.Exporter` — gated on
`APPLICATIONINSIGHTS_CONNECTION_STRING` being present and non-empty. The OTLP exporter
stays exactly as it was, so local Aspire dashboard behaviour is unchanged; the two paths
coexist and are selected by which environment variable is present.

The connection string is passed **explicitly** through each exporter's options callback:

```csharp
metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
```

rather than relying on the SDK's environment-variable autodiscovery.

`infra/modules/shared.bicep` supplies `APPLICATIONINSIGHTS_CONNECTION_STRING` to every
`scheduled-cli-job` caller, matching what the three container apps already received.

### Why the exporter package, not the AspNetCore distro

The distro instruments the ASP.NET Core request pipeline and would not attach in the CLI
job host or the worker — the hosts whose telemetry #840 was about. The bare exporter
package is host-agnostic and composes with the OpenTelemetry registration
`ServiceDefaults` already owns, so one code path serves all four hosts.

### Why the connection string is passed in code, not autodiscovered

The Azure Monitor SDK reads `AzureMonitorExporterOptions.ConnectionString` at
registration time and, when that field is empty, falls back to reading the **process
environment**. A value supplied through `IConfiguration` — which is how this repo
configures everything, and how a test supplies one via
`AddInMemoryCollection` — is invisible to that fallback, because it never touches the
process environment. The exporter then throws "A connection string was not found."

That is not a hypothetical: it is what made the first attempt at a
`ServiceDefaults` test fail. Passing the value explicitly makes the wiring
**configuration-driven and therefore testable**, and removes a silent dependency on how
the value happens to reach the process. `OpenTelemetryExporterTests` asserts the gate in
both directions as a result.

## Consequences

- **Bicep env changes require a stack run.** Adding the variable to the job templates
  takes effect only after `Deploy-SharedResources.ps1`; an image-only merge leaves jobs
  exporting nothing, because the ACA job image and its env both come from the deployment
  stack rather than the `Deploy` workflow. Tracked as #859, and the reason #840's
  acceptance stayed open after the code merged.
- **Two exporters now exist in one codebase.** Local runs export OTLP to the Aspire
  dashboard; deployed runs export to Azure Monitor. The invariant that survives both is
  the *instrument names* — `Meter` and `ActivitySource` names are unchanged, so queries
  written against `pinwiz.*` remain valid across environments. `docs/observability.md`
  was corrected to state this (#849).
- **A provider must actually exist to export anything.** The exporters are attached to
  providers that OpenTelemetry creates from a hosted service, so a host that is never
  started exports nothing regardless of this wiring. That is a separate defect, fixed for
  the CLI in #852 and guarded by `OpenTelemetryHostLifecycleTests`.
- **ADR-0014's SDK pin is superseded on this one point.** Its
  `Azure.Monitor.OpenTelemetry.AspNetCore` line is annotated to point here; the rest of
  ADR-0014 stands.
- If a future host genuinely needs ASP.NET Core-specific auto-instrumentation, adding the
  distro *alongside* this wiring would double-register exporters. Prefer extending
  `ServiceDefaults` over introducing the distro.

## References

- #840 — the gap: all `pinwiz.linker.*` metrics unobservable in production
- #846 — exporter + env wiring; #849 — the `docs/observability.md` truth correction
- #852 — host lifecycle fix (providers are never built if the host never starts)
- #859 — merge-time deploy does not repoint ACA job images
- [ADR-0014](0014-microsoft-foundry-orchestration.md) — SDK pins (partially superseded here)
