# Golden Link Set — Capture Record

**STATUS: NOT YET CAPTURED — capture is operator-gated (see below).**

This directory will hold `golden-link-set.captured.json` once the operator runs
the capture command against the fully re-linked live corpus. The replay test
(`GoldenLinkSetReplayTests.GoldenLinkSet_Replays_WithNoMisattribution`) skips
explicitly when the file is absent so that "never captured" is never confused
with "passed".

## What this fixture will contain

A JSON array of linked-document bindings captured from `scraped_documents_raw`
(link_status in Linked / ManuallyLinked) at capture time. Each entry records the
document ID, file URL, source type, game slug, document type, manufacturer key,
and the machine ID the current linker resolved it to. The replay test rebuilds a
`Pending` `RawDocumentRecord` from these fields and runs it through `DocumentLinker`
to verify the binding is still reproduced.

| Field | Value (at capture time) |
|---|---|
| Source | live Cosmos `scraped_documents_raw` (link_status = Linked / ManuallyLinked) |
| Captured at | NOT YET CAPTURED |
| Documents | NOT YET CAPTURED |
| Fan-out entries | NOT YET CAPTURED |

## To capture

Run **after** `--relink-all` has produced a stable, fully re-linked corpus:

```bash
export AZURE_TOKEN_CREDENTIALS=dev
export Cosmos__AccountEndpoint="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
dotnet run --project src/PinballWizard.Cli -c Release -- --capture-golden-set
```

This writes `golden-link-set.captured.json` alongside this file and updates this
`CAPTURE.md` with live counts and a timestamp.

## Outcome policy enforced by the replay test

- `linked -> different machine` = BLOCKING failure (mis-attribution — provenance is sacred)
- `linked -> needs_review` (linker returns no machine) = reportable; does NOT fail
- `not_in_catalog -> linked` = a WIN; reported as improvement, does NOT fail

## References

- ADR-0054 unified machine resolution
- S3 task brief (docs/superpowers/plans/2026-07-13-unified-machine-resolution-wave1.md)
- TEST-05 (.claude/standards/testing/STANDARD.md)
