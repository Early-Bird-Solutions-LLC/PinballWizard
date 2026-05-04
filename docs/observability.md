# Observability

The OpenTelemetry inventory for PinballWizard. Captures every metric, activity, and standard tag the project emits, plus the pattern Phase 3 / 4 / 5 services follow when adding new instruments.

Read alongside [`build-spec.md`](build-spec.md) Phase 2 § Scope item 5 (the scope entry that produced this doc) and [`quality-spec.md`](quality-spec.md) § "Operational quality" (the SLO targets these metrics back).

## Pipeline summary

- **Meter and ActivitySource:** [`PinballWizard.Application.Observability.PinballWizardTelemetry`](../src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs) is the single project-wide source of metrics and traces. Both are named `"PinballWizard"`. New instruments — counters, histograms, activities — live alongside the existing ones in this static class.
- **Registration:** [`PinballWizard.ServiceDefaults`](../src/PinballWizard.ServiceDefaults/Extensions.cs) registers the Meter via `AddMeter("PinballWizard")` and the ActivitySource via `AddSource("PinballWizard")` in its `ConfigureOpenTelemetry`. The string literal is duplicated in ServiceDefaults rather than referencing the typed constant — a typed reference would invert the layering (ServiceDefaults → Application). The duplication is documented in both files.
- **Exporter:** the Aspire dashboard injects `OTEL_EXPORTER_OTLP_ENDPOINT` when running under `start-apphost.ps1`, which makes ServiceDefaults wire `UseOtlpExporter()`. Container Apps (Phase 5+) will inject the same env var pointing at Application Insights' OTLP endpoint, so the exporter wiring is unchanged across environments.
- **Where signals land today:** Log Analytics (via Cosmos diagnostic settings — Phase 1 Bicep). Aspire dashboard locally.
- **Where signals will land (Phase 6+):** Application Insights, once Phase 2 Bicep flips. Same OTLP exporter + Meter / Source names continue to work; only the destination changes.

## Metric inventory

### OPDB sync (Phase 2 § Scope item 5)

All counters carry a `pinwiz.opdb.sync.mode` attribute — `"apply"` for real runs, `"dry_run"` for projection runs — so dashboards can filter operational charts to apply-only and pre-deploy validation runs to dry-run-only.

| Instrument | Type | Unit | Description |
| --- | --- | --- | --- |
| `pinwiz.opdb.sync.fetched` | Counter\<long> | `{record}` | OPDB records fetched from the API across all sync runs |
| `pinwiz.opdb.sync.inserted` | Counter\<long> | `{machine}` | Machines newly inserted into the repository (or projected-insert in dry-run) |
| `pinwiz.opdb.sync.updated` | Counter\<long> | `{machine}` | Existing machines updated with merged OPDB fields (or projected-update in dry-run) |
| `pinwiz.opdb.sync.skipped` | Counter\<long> | `{record}` | OPDB records skipped because they failed validation or mapping |
| `pinwiz.opdb.sync.failed` | Counter\<long> | `{run}` | OPDB sync runs that aborted with an exception |
| `pinwiz.opdb.sync.duration_ms` | Histogram\<double> | `ms` | Wall-clock duration of an OPDB sync run |

**Emission cadence:** all counters and the histogram emit a single observation **per run** (in the `finally` block of `OpdbSyncService.SyncAsync`). Per-record observations would multiply observation overhead and balloon cardinality without operational benefit at the current 9-source scale. When per-source metrics become valuable (Phase 3+), add a `pinwiz.source` attribute rather than fanning into per-source instruments.

## Activity inventory

| Activity name | Source | Captured tags |
| --- | --- | --- |
| `pinwiz.opdb.sync` | `PinballWizard` | `pinwiz.opdb.sync.mode`, `pinwiz.opdb.sync.fetched`, `pinwiz.opdb.sync.inserted`, `pinwiz.opdb.sync.updated`, `pinwiz.opdb.sync.skipped`, `pinwiz.opdb.sync.duration_ms`. On exception: `ActivityStatusCode.Error` + the exception's `Message` |

Activities cover one OPDB sync invocation end-to-end. The trace tags duplicate the per-run metric observations so a trace alone tells the run's full story without joining against the metric stream.

## IngestionSource write-back

Independent of the OTel pipeline, `OpdbSyncService` writes per-run state back to the source's Cosmos `IngestionSource` document via `IIngestionSourceRepository.RecordRunResultAsync`:

| Field on `IngestionSource` | Behavior on apply run | Behavior on dry-run |
| --- | --- | --- |
| `LastRunAt` | Set to run start time | **Not modified** (dry-run shouldn't update operator-visible "last run" timestamps) |
| `LastSuccessAt` | Set to run start time on success; preserved on failure | Not modified |
| `TotalDocumentsDiscovered` | Incremented by `inserted + updated` | Not modified |
| `TotalRunFailures` | Incremented by 1 on failure; unchanged on success | Not modified |

This write-back is the only metric path that distinguishes between apply and dry-run at storage level — the OTel counters use the `pinwiz.opdb.sync.mode` attribute for the same purpose at observation level.

A write-back failure does **not** mask the original sync outcome — it's caught and logged at error level inside the `OpdbSyncService.SyncAsync` finally. The source's `lastRunAt` may lag by one run; the next run reconciles.

## How to consume

### Locally (Aspire dashboard)

```pwsh
pwsh ./start-apphost.ps1
```

The dashboard URL printed in the AppHost output (default `https://localhost:17110`) renders the Meter and ActivitySource live. Counters chart over time; histograms render bucket distributions; activities show as traces with the captured tags inline.

### Deployed (Log Analytics today)

OTLP signals flow into Log Analytics via Cosmos diagnostic settings (Phase 1 Bicep). Query examples:

```kusto
// OPDB sync run summary, last 24h, apply mode only
AppMetrics
| where Name startswith "pinwiz.opdb.sync."
| where Properties["pinwiz.opdb.sync.mode"] == "apply"
| where TimeGenerated > ago(24h)
| summarize Total=sum(Sum) by Name, bin(TimeGenerated, 1d)

// Failed runs in the last week
AppMetrics
| where Name == "pinwiz.opdb.sync.failed"
| where TimeGenerated > ago(7d)
| summarize Failures=sum(Sum) by bin(TimeGenerated, 1d)
```

> **Table-name footnote:** the destination table depends on the OTLP ingestion path. Container Apps' direct Log Analytics workspace surfaces metrics under `AppMetrics`; Application Insights' classic OTel ingestion (Phase 6+ when AI is provisioned) surfaces them under `customMetrics`. If a query returns no rows, swap the table name and re-run; the column shapes are similar enough that the rest of the query lands.

### Deployed (Application Insights — Phase 6+)

Once Phase 2 Bicep flips and App Insights is provisioned, the same OTLP exporter writes there. KQL queries port directly; UI charts pick the metric names from the Meter automatically.

## Adding new instruments (Phase 3 / 4 / 5 pattern)

When a new service or scraper needs instrumentation:

1. **Add the instrument to [`PinballWizardTelemetry`](../src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs).** Use the `pinwiz.<domain>.<operation>.<measure>` naming convention — e.g., `pinwiz.scrape.run.documents_discovered`, `pinwiz.rag.query.latency_ms`, `pinwiz.wizard.answer.citation_count`.
2. **Set unit + description.** `unit` is OTel UCUM (`{record}`, `{user}`, `ms`, `By`, etc.); `description` is a one-sentence explanation that appears in dashboards.
3. **Tag with attributes, not separate instruments.** A per-source counter is `pinwiz.scrape.run.documents_discovered{source="jjp"}`, NOT `pinwiz.scrape.jjp.run.documents_discovered`. Attribute cardinality stays bounded (8 sources × 2 modes = 16 series); per-source instruments multiply the inventory.
4. **Update this doc.** Add the new instrument to the inventory table in the same PR. The `PinballWizardTelemetryTests` pinning tests catch instrument-name typos at build time; this doc catches them at review time.
5. **Update the Aspire dashboard or Log Analytics dashboard if applicable.** A new metric without a chart is invisible.

## Standard tags

When emitting a metric or activity, prefer these attribute keys to maximize cross-service queryability:

| Attribute key | Type | Notes |
| --- | --- | --- |
| `pinwiz.<domain>.<operation>.mode` | string | `apply` / `dry_run` for operations that have those shapes |
| `pinwiz.source` | string | Source key from `IngestionSource.id` (e.g., `stern`, `jjp`, `opdb`) |
| `pinwiz.partition_key` | string | Cosmos partition key (when relevant) |
| `pinwiz.container` | string | Cosmos container name (when relevant) |

OTel semantic conventions (`db.*`, `messaging.*`, `http.*`) cover their respective surfaces — no need to re-invent. Use those for the kinds of operations they cover; use `pinwiz.*` for project-specific concepts (sync runs, citations, refusals, etc.).

## Deferred to later phases

- **Cosmos RU charge capture** (`pinwiz.cosmos.write.ru_charge`) — Phase 6 (operability). Requires either a `MeteredMachineRepository` decorator or an inline RU-capture helper in repositories. Best designed once real production traffic gives signal on which operations dominate RU consumption. Tracked under Phase 6 § Cost quality.
- **Per-scraper run metrics** (`pinwiz.scrape.<source>.*`) — Phase 3+. Lands when manufacturer scrapers gain ACA Job execution and the orchestrator-from-IngestionSource path comes online.
- **Wizard-side metrics** (`pinwiz.wizard.query.*`) — Phase 4 (RAG). Latency, citation count, refusal rate, retrieval-quality evals.

## Update triggers

Per [`guardrails.md`](guardrails.md) § Spec maintenance, this doc updates **in the same PR** as the work it describes:

- Adding a new instrument: this doc grows by one row; `PinballWizardTelemetryTests` gains a pinning assertion.
- Renaming an instrument: this doc updates; the test updates; dashboards and alert rules listed here are updated.
- Removing an instrument: this doc loses a row; the test loses an assertion; no dashboard references remain.
