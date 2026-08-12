# 0007 — Per-manufacturer ingestion sources are Cosmos data, not Bicep config

**Status:** Accepted
**Date:** 2026-05-02

## Context

Phase 2's plan includes scrapers for many pinball manufacturers — Stern
(already built), JJP, American Pinball, Spooky, Multimorphic, Chicago
Gaming, Haggis, Pinball Brothers, Dutch Pinball, Barrels of Fun, with
more likely to follow. Each manufacturer has:

- A scraper implementation (concrete `ISourceScraper` in
  `Infrastructure/Scraping/<Mfg>/`)
- A schedule (daily / weekly / monthly)
- Politeness overrides (per-source delay, robots.txt path, User-Agent
  string)
- An enabled/disabled state
- Operational state — last run, last success, telemetry counters

We need a place to store the per-manufacturer state. Two natural
options:

1. **Bicep / IaC parameters** — the schedule and enabled flag are
   parameters of each ACA Job; changing one requires a redeploy.
2. **Runtime data in Cosmos** — each manufacturer is a row in a Cosmos
   container; the Admin UI edits the row at runtime; the ACA Jobs read
   their config at startup.

## Decision

The per-manufacturer state lives in a **Cosmos container
`ingestion_sources`**, not in Bicep. Each manufacturer is a document
with:

- `id` — the manufacturer key (e.g., `"stern"`, `"jjp"`, `"opdb"`)
- `displayName` — human-readable (e.g., `"Stern Pinball"`)
- `scraperImplKey` — maps to a registered concrete `ISourceScraper`
  implementation in DI
- `baseUrl` — the source-site root URL
- `enabled` — boolean toggle
- `cadence` — `"daily"` / `"weekly"` / `"monthly"` / `"manual"`
- `politenessOverrides` — per-source delay floor, robots.txt path
  override, User-Agent suffix
- `lastRunAt`, `lastSuccessAt`, telemetry counters

The Bicep still creates **one ACA Job per manufacturer** (for failure
isolation and for parallelism across origins, which is consistent with
the polite-scraping ethos: politeness is per-origin, not per-process).
But each ACA Job reads its config from Cosmos at startup, not from
Bicep parameters.

The Admin UI (`/admin/ingestion-sources`, MudBlazor `MudDataGrid`,
behind Entra `GlobalAdmin` role per ADR 0009) lets a maintainer:

- Enable / disable a source
- Change the cadence
- Edit politeness overrides

— all without a deployment.

Adding a **new** manufacturer is still a code change (new
`ISourceScraper` impl) plus a Bicep change (new ACA Job referencing
the existing common image with `--source <impl-key>`) plus a new row
in `ingestion_sources`. The first two are deployable infrastructure;
the third is a database write.

## Consequences

**Positive:**
- **Operational agility.** Disabling a misbehaving source is a
  database flip, not a redeploy. Adjusting a politeness delay because
  a source-site operator complains is the same.
- **Telemetry lives with config.** `lastRunAt`, `lastSuccessAt`, and
  counter increments are co-located with the toggle that's about to
  flip based on them.
- **Admin UI is meaningful.** The IngestionSources MudDataGrid does
  real work, not just reads — that's what justifies it being a v1
  feature alongside the public Wizard rather than deferred.
- **Bicep stays thin.** Bicep describes *capacity* (ACA Job exists,
  bound to image, with managed identity); Cosmos describes *posture*
  (is it on, what's its cadence, what's its politeness profile).

**Negative:**
- **Operations require Cosmos read on startup.** If Cosmos is
  unavailable when a scraper boots, the scraper aborts cleanly. This
  is acceptable — Cosmos availability is also required for the
  scraper to write its results.
- **Schema evolution requires migration discipline.** Adding a new
  `politenessOverrides` field requires either a default-on-read or a
  one-shot migration. Ordinary Cosmos schema-evolution patterns apply.
- **Auditability of config changes** — Cosmos isn't a source-control
  system. We rely on Cosmos's own change feed (which we're already
  consuming for AI Search ingestion per Phase 4) to surface admin
  edits if/when audit becomes a requirement.

## Alternatives considered

- **Bicep parameters everywhere.** Rejected — every ops decision
  becomes a redeploy. Too slow for the polite-scraping reality, where
  responding to a source-site change might need to happen within
  hours.
- **Hybrid: enabled-flag in Bicep, cadence/politeness in Cosmos.**
  Rejected as the worst of both worlds — one toggle in code, another
  in data, and ops need to remember which is where.
- **Configuration as code in Git, deployed separately from infra.**
  Rejected — overkill for a single-tenant project with one operator;
  we'd build a config-deployment pipeline that exists to change three
  fields per row.

## References

- [`docs/infra_analysis.md`](../infra_analysis.md) §7 — implementation
  detail.
- `project_phase2_architecture_decisions.md` (private project memory, not in this repo)
  — IngestionSources locked decision.
- ADR 0009 — Entra External ID gating the Admin UI.
