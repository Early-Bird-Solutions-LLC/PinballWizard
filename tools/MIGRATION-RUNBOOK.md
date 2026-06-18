# Live-migration runbook (AB#259 and future data migrations)

Hard-won process rules from the AB#259 edition-scope migration. Two failures on the
first OPDB re-sync motivated these; both are now guarded.

## Rule 1 — Pre-flight before any long-running step (FAIL FAST)

A multi-hour step (OPDB sync ≈ 5h at polite pacing) must verify its preconditions
**before** starting, never discover a problem at the end.

```pwsh
pwsh tools/migration-preflight.ps1 -RebuildIfStale
```

Checks: **binary freshness** (the compiled dll must be newer than every `src/**/*.cs`
— a stale binary silently runs old logic), no uncommitted `src/` drift, `az login` on
the right sub, required env vars present. Exit non-zero ⇒ do **not** start the step.

**Why:** the first run was *almost* derailed by a binary built before a `git pull`
merge rewrote the source. Always rebuild after a branch switch / merge / pull, and let
the pre-flight confirm it.

## Rule 2 — Verify gate outcomes through the PRODUCTION code path, never a reimplementation

A diagnostic probe MUST exercise the same code the app uses. Reimplementing a
transform in the probe will eventually drift from production and produce a false
result.

**Concrete trap that cost ~2h this migration:** a probe normalized lookup keys with
`ScraperReconciliationService.NormalizeTitle` (strips all non-alphanumerics →
`"godzillapro"`), but the lookup repository keys rows with
`MachineTitleLookup.NormalizeTitle` (replaces only `/ \ ? #`, **keeps spaces** →
`"godzilla pro"`). The probe reported the rows as missing when they existed — a
phantom failure that triggered a wrong root-cause hunt.

**Rule:** when a probe must replicate a production transform, copy it **verbatim from
the production type** (cite the source: `MachineTitleLookup.NormalizeTitle`,
`src/PinballWizard.Core/Domain/MachineTitleLookup.cs`) or, better, invoke the
production path (`--ask` for `getMachineByTitle`, the real repository, etc.). If a
probe returns "absent/missing", **first re-verify the key/normalizer against source**
before concluding the data is wrong.

## Rule 3 — Each gated step verifies its OWN output before the next step starts

The migration is intentionally gated (Step 1 → 2 → 3 → 4 → 5 → 6). Each step's gate
runs a read-only probe and must pass before proceeding. Destructive steps (index
rebuild, row deletes) pause for explicit operator go-ahead. Never chain a destructive
step onto an unverified prior step.

## Rule 4 — No throttling on internal Azure calls

The politeness framework (`IPolitenessGate`, `PoliteScraperBase`, per-host delays) exists
exclusively for **outbound HTTP to external sites** (manufacturer domains, OPDB API, S3).
It must never be applied to calls within our own Azure infrastructure.

Internal calls that run at full speed:

- Azure OpenAI embedding calls (`AzureOpenAIChunkEmbedder.EmbedBatchAsync`)
- AI Search upsert batches (`AiSearchRagIndexer`)
- Cosmos DB reads/writes
- Any other Azure-to-Azure call within our subscription

If an internal Azure service returns 429, the correct response is **retry-with-backoff**
honouring the `Retry-After` header (already implemented in `AzureOpenAIChunkEmbedder`) —
not a politeness gate or artificial delay. Concurrency is controlled by quota-aware options
(`EmbeddingMaxConcurrency`, `BackfillConcurrency`) tuned to our provisioned TPM, not by
a throttle that treats Azure like an external site.

**Why:** adding a politeness gate to an Azure embedding call would be a category error —
it would slow the backfill to external-site pacing (~0.5 req/s) when we're provisioned
for 350k tokens/minute.

## Known follow-ups from AB#259 backfill (both block "process every document")

### Follow-up A — Embedder retry-with-backoff (429 RateLimitReached)

**File:** `src/PinballWizard.Infrastructure/Rag/Indexing/AzureOpenAIChunkEmbedder.cs`

**Problem:** When a very large doc (400+ chunks) sends a rapid burst of embedding batches it
can momentarily saturate our 350k TPM allocation. The `ClientResultException` (HTTP 429)
currently propagates all the way up to `CosmosRagBackfillService` which marks the whole
document as failed. Azure always returns a `Retry-After` header (typically 1–2 s).

**Fix:** In `EmbedBatchAsync`, catch `ClientResultException` with status 429, read the
`Retry-After` response header (fall back to 2 s if absent), `await Task.Delay`, retry the
same batch in-place. Cap retries at 3. This keeps the failure isolated to the embedding
layer and invisible to the pipeline/backfill service.

**Impact:** Eliminates the ~5–10% document failure rate on large docs during backfill.
Subsequent backfill passes work around it today but it should be fixed so a single pass
suffices.

### Follow-up B — OCR fallback for scanned-image PDFs (OcrRequired)

**File:** `src/PinballWizard.Application/Rag/Extraction/` (extractor pipeline)

**Problem:** Some manufacturer manuals are pure scanned images. `PdfPigDocumentTextExtractor`
returns 0 chars across N pages and classifies them as `OcrRequired`. The pipeline skips
them with `Skipped_ExtractionFailed`. They cannot be indexed until text is extracted.

**Fix:** Wire Azure Document Intelligence as a fallback extractor — when PdfPig returns
`OcrRequired`, pass the stream to the ADI "prebuilt-read" model. Already planned as
Phase 4.5.

**Impact:** ~10–15 scanned docs currently unindexable. Relevant machines have no searchable
content until Phase 4.5 lands.

## OPDB sync timing note

`--source opdb` makes one **polite 10s-paced** `/api/machines/{segment}` call per
distinct group segment (~1,205 segments) → ≈5h wall-clock, ~mostly idle. Only
`/api/export` is disk-cached (`data/cache/opdb-export.json`); group calls are not, so a
re-run pays the full time again. This is by design (polite-by-construction); budget for
it. `editionTokens` derive from the bulk export (fast); the per-segment calls only
resolve the franchise `Title`.
