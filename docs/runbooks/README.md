# Runbooks

Operational runbooks for PinballWizard / pinwiz.ai. Each file is self-contained — short enough to skim in under two minutes during an incident, detailed enough to execute without additional context.

Read alongside [`docs/observability.md`](../observability.md) (metric and instrument inventory) and [`docs/build-spec.md`](../build-spec.md) § Phase 6 (the operability spec that defines the alert thresholds these runbooks respond to).

## Runbook index

| File | Scenario | Triggers |
| --- | --- | --- |
| [`01-incident-response.md`](01-incident-response.md) | Wizard is down or severely degraded — first 30 minutes | `pinwiz-alert-availability` or `pinwiz-alert-5xx-rate` fires |
| [`02-cost-anomaly.md`](02-cost-anomaly.md) | Unexpected spend spike | `pinwiz-alert-daily-cost` fires |
| [`03-cosmos-restore.md`](03-cosmos-restore.md) | Catalog corruption or data loss — restore from backup | Data integrity concern, Cosmos errors, or DR drill |
| [`04-ai-search-rebuild.md`](04-ai-search-rebuild.md) | AI Search index corrupt, out of sync, or schema-breaking change | Dead-letter depth alarm, index schema migration, or DR drill |
| [`05-secret-rotation.md`](05-secret-rotation.md) | Rotate AI keys, Cosmos keys, Cloudflare token, OPDB token | 90-day rotation cadence or key compromise |
| [`06-source-site-outage.md`](06-source-site-outage.md) | Upstream scraper source returns 403/429/5xx or changes `robots.txt` | Source error rate spike or `robots.txt` change detected |

## Freshness requirement

Each runbook carries a `Last walked:` date in its header. Runbooks older than 6 months are flagged in the monthly self-evaluation cadence (`guardrails.md` § Self-evaluation cadence) and re-validated before the next DR drill window.

## Pre-launch gate

All six runbooks must exist and have a `Last walked:` date within 30 days of the public launch date. This is item 6 of the `guardrails.md` § Pre-public-launch gate and Phase 6 scope item 1.

## Existing operational runbooks

The [`h-chain-operator-runbook.md`](h-chain-operator-runbook.md) file in this directory documents the Phase 3/4 H1/H2/H3 operator hand-off chain. It is a development-phase operational artifact, not a steady-state incident-response runbook — it is referenced here for completeness but is not part of the pre-launch gate checklist.
