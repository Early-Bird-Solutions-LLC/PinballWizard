# Reconciler Parity Snapshot — Capture Record

**STATUS: NOT YET CAPTURED — capture is operator-gated (see below).**

This directory will hold `reconciler-parity.captured.json` once the operator runs
the capture command against the live Cosmos machines container. The replay test
(`ReconcilerParityReplayTests.ReconcilerParity_Replays_WithNoSlugCountDrop`) skips
explicitly when the file is absent so that "never captured" is never confused
with "passed".

## What this fixture will contain

A JSON snapshot of per-manufacturer `ManufacturerSlugs` state captured from the
live Cosmos `machines` container. Each entry records a machine ID, title,
manufacturer key, and the slug that was present at capture time. The replay test
seeds a mock `IMachineRepository` with these machines, builds a `GameCatalog`
matching the captured slugs, and asserts `ScraperReconciliationService.ReconcileAsync`
returns `MatchedBySlug >= capturedSlugCount`. A normalization regression or
wrong-key lookup shows up as a count drop.

| Field | Value (at capture time) |
|---|---|
| Source | live Cosmos `machines` container |
| Captured at | NOT YET CAPTURED |
| Total machines | NOT YET CAPTURED |
| Machines with slugs | NOT YET CAPTURED |

## To capture

Run **after** a full OPDB sync + scraper reconciliation pass:

```bash
export AZURE_TOKEN_CREDENTIALS=dev
export Cosmos__AccountEndpoint="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
dotnet run --project src/PinballWizard.Cli -c Release -- --capture-reconciler-parity
```

This writes `reconciler-parity.captured.json` alongside this file and updates this
`CAPTURE.md` with live counts and a timestamp.

## Outcome policy enforced by the replay test

- `MatchedBySlug < capturedSlugCount` = BLOCKING failure (normalization regression)
- `MatchedBySlug == capturedSlugCount` = PASS
- `MatchedBySlug > capturedSlugCount` = WIN (reported; cannot happen from this fixture alone)

## References

- ADR-0054 unified machine resolution
- S3 task brief (docs/superpowers/plans/2026-07-13-unified-machine-resolution-wave1.md)
- TEST-05 (.claude/standards/testing/STANDARD.md)
