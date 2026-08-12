# Linker extraction memory — design

**Date:** 2026-08-12
**Issue:** [#832](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/832)
**Status:** approved, not yet implemented

## Problem

The nightly linker ACA job fails. The 2026-08-12 02:00 run reported:

```
DocumentDownload complete: downloaded=0 skipped=1572 failed=1 skippedTooLarge=76 backfilled=0
DocumentLinker: exception linking doc_4ea5f0c438428b8b.
System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.   (x5)
--link-documents complete: processed=228 linked=0 not_in_catalog=227 failed=1 needs_review=0
```

`doc_4ea5f0c438428b8b` is `chicagogaminggamepage/MB_Manual_Rev_1.pdf`, 42 MB — well under the
500 MB download cap, so it downloads fine and then dies during text extraction.

Since #819 bucketed the 76 permanently-oversized downloads into their own `skippedTooLarge`
counter, this is the sole remaining cause of a red nightly.

## Root cause

Not one defect. Three compounding memory sinks, none of them bounded, multiplied by a
concurrency setting that was never meant to govern parsing.

### 1. The blob is fully materialized before parsing

`BlobDocumentStore.OpenReadAsync` / `TryOpenReadAsync` download into a `MemoryStream` to give
PdfPig the seekable stream it requires. A 42 MB blob costs more than 42 MB transiently:
`MemoryStream` grows by doubling, so the peak includes both the old and new buffers, and buffers
this size land on the Large Object Heap.

### 2. The linker materializes every page to read two

`DocumentLinker` consumes only page 0 and page 1 — tier 3 (`page_1`,
`DocumentLinker.cs:290`) and tier 4 (`page_2`, `DocumentLinker.cs:300`). But
`PdfPigDocumentTextExtractor.Extract` builds a `List<ExtractedPage>` covering every page **and**
a `StringBuilder` holding the entire concatenated document text, then returns both. For a large
manual this retained text is plausibly a bigger sink than the file buffer, and for this caller
it is 100% waste.

### 3. The size guard cannot bound memory where it sits

`ExtractAsync` rejects streams over `PdfExtractionOptions.MaxStreamBytes` (default 100 MB) with
`ExtractionStatus.SizeExceeded` — but it decides by reading `pdfStream.Length`, which is only
available *after* the whole blob is already in memory. As a memory bound it is inert for exactly
this failure mode.

### 4. Concurrency 20 multiplies all of the above

`RunBatchAsync` runs `Parallel.ForEachAsync` with
`MaxDegreeOfParallelism = _cosmosWriteConcurrency`, defaulting to **20**
(`ScraperSettings.CosmosWriteConcurrency`). The linker ACA job is **0.5 vCPU / 1 GiB**
(`deploy/scheduled-cli-job/scheduled-cli-job.bicep`). So peak extraction memory is
`O(20 × document size)` — a knob tuned for Cosmos *write* throughput silently governs PDF
*parse* memory.

`BlobDocumentStore`'s own comment justifies buffering because "the largest raw document in scope
(~80 MB Stern Godzilla service manual) fits inside the ACA container's 1 GiB memory limit with
room to spare". That reasoning is correct for one document and never considered twenty.

### Why five OOMs but only one failure

`replicaRetryLimit: 0` on the job, so there are no retries — the five exceptions were five
concurrent workers inside the same memory-pressure window, not one document attempted five
times. The exact failure-count accounting is worth confirming against the real logs during
implementation, but the design below does not depend on the answer.

### Why it surfaced as a batch-level error

`TryExtractDocumentAsync` opens the blob at `DocumentLinker.cs:773`, **outside** the `try` that
begins at line 780. So an OOM during buffering bypassed the per-document handler and escaped to
the batch-level `catch` at line 433, logging `exception linking` rather than the extraction
path's own `text extraction failed for {DocId}`.

## Non-goals

- **Raising the container memory limit.** Explicitly rejected by `.claude/rules/timeout-debugging.md`
  (masking a symptom rather than fixing the cause). The fix must make memory flat with respect to
  document size and count.
- **`FileDownloader`'s buffering.** `FileDownloader.cs:145` cites the same stale headroom
  analysis, but it is the download path with its own 500 MB cap and a different risk profile.
  Follow-up issue, not this change.
- **Tier 5 OCR.** Still deferred.

## Design

### A. Temp-file-backed reads, behind the existing interface

`BlobDocumentStore.OpenReadAsync` / `TryOpenReadAsync` keep their signatures, their
"seekable, positioned at 0" contract, and their 404→null / 404→throw split. The implementation
changes: download into a temp `FileStream` opened with `FileOptions.DeleteOnClose` instead of a
`MemoryStream`. Peak memory becomes `O(copy buffer)` regardless of blob size.

Both callers already consume the result as a plain `Stream` inside `await using` — no
`MemoryStream` casts exist — so deletion is automatic, and `DeleteOnClose` means the filesystem
drops the file even on abnormal termination. Their comments asserting "returns a seekable
`MemoryStream`" (`BlobDocumentBytesSource.cs:105`, `BlobDocumentStore.cs:13`) must be corrected
in the same change.

This also removes the same buffering from `DocumentDownloadService`'s SHA-256 backfill, which
today streams a hash over a fully-materialized blob.

**Alternative considered — blob range-streaming.** `BlobBaseClient.OpenReadAsync` returns a
`LazyLoadingReadOnlyStream`, verified against the SDK source at tag
`Azure.Storage.Blobs_12.29.1`: `CanSeek` returns `true`, `Seek` is fully implemented (reusing
the buffer in range, invalidating and re-fetching outside it), and `BlobOpenReadOptions.BufferSize`
bounds memory. It is viable and needs no disk. Rejected because PdfPig seeks widely — the xref
table is at EOF and object streams are scattered — so every out-of-buffer seek issues a fresh
range GET. That trades a known OOM for an unmeasured latency risk. A temp file makes those seeks
local and predictable.

### B. `ExtractPreviewAsync` returning a distinct `ExtractedPreview`

New **required** method on `IDocumentTextExtractor`:

```csharp
Task<ExtractedPreview> ExtractPreviewAsync(Stream pdfStream, int pageCount, CancellationToken ct);
```

`ExtractedPreview` carries `Status`, the first N `ExtractedPage`s, and `Error` — deliberately
**no** whole-document `Text` and **no** `Outline`. That omission is what eliminates sink #2.

- `PdfPigDocumentTextExtractor` implements it with `document.GetPage(i)` for
  `i in 1..min(pageCount, NumberOfPages)`. Verified against PdfPig source at tag `v0.1.15`
  (`src/UglyToad.PdfPig/Content/Pages.cs`): `GetPage` resolves a single page node and calls
  `pageFactory.Create` for that page only — construction is strictly on demand, with no page
  cache. Requesting two pages therefore parses two pages, not all of them.
- `AzureDocumentIntelligenceExtractor` honors the limit via its page-range parameter.
- `FallbackDocumentTextExtractor` forwards, falling back on the same conditions as `ExtractAsync`.
- `DocumentLinker` calls it with `pageCount: 2`, matching the only two page tiers it has.

The preview path deliberately does **not** apply the `OcrRequiredCharFloor` heuristic. That
classification exists for the indexing path; a linker preview yielding empty text simply produces
no evidence and the tier declines, which is the honest outcome.

**Why a distinct type rather than a `maxPages` parameter.** A required method is
compiler-enforced across all three implementations, so an implementation that ignores the limit
cannot silently compile and keep OOMing. And because `ExtractedPreview` is not an
`ExtractedDocument`, a truncated parse is *type-incompatible* with the chunking/indexing path —
a partial document cannot be indexed as if complete. The bad state is unrepresentable rather
than merely discouraged, which is the posture `.claude/rules/sdd-artifact-hygiene.md` argues for.

### C. Size guard moves upstream

`DocumentLinker.TryExtractDocumentAsync` calls the already-present
`IDocumentBlobStore.GetSizeAsync` (a blob-properties call, no body download) and returns a
`SizeExceeded` skip **before** opening anything — so an oversized blob is never transferred to
container disk. This matters because ACA ephemeral storage at this job size is finite and blobs
may be up to the 500 MB download cap.

The extractor keeps its `.Length` check as defence in depth; it is now meaningful, because the
stream is disk-backed by the time it runs. `PdfExtractionOptions.MaxStreamBytes` remains the
single source of the threshold — one value, two enforcement points, no duplicated constant.

### D. Dedicated extraction gate

New `ScraperSettings.ExtractionConcurrency`, default **4**, applied as a `SemaphoreSlim` around
the entire open-plus-parse span in the linker — bounding temp-disk and memory together.
`Parallel.ForEachAsync` stays at `CosmosWriteConcurrency = 20` for the cheap I/O-bound write
path.

This decouples two genuinely different costs. Lowering the shared setting instead would throttle
Cosmos writes for the ~90% of documents that never reach extraction, and would re-couple them so
that the next person tuning write throughput silently re-breaks memory.

A byte-budget weighted semaphore was considered and rejected as YAGNI: it needs a
bytes→peak-memory model that PdfPig does not make predictable.

### E. Move the blob open inside the try

`TryExtractDocumentAsync` opens the blob outside its own `try`. Moving the open inside makes any
open-path failure degrade visibly for that one document — `Failed` with a reason, batch
continues — instead of escaping to the batch-level handler. This satisfies the issue's secondary
requirement directly.

## Testing

The OOM cannot be reproduced locally: a dev box has far more than 1 GiB, and the trigger is
twenty concurrent workers against a container cap. Tests therefore assert **mechanism**, not the
absence of an OOM.

| Unit under test | Assertion |
|---|---|
| `BlobDocumentStore` | returned stream is seekable and not a `MemoryStream`; temp file is gone after dispose; `TryOpenReadAsync` still returns `null` on 404; `OpenReadAsync` still throws on 404 |
| `PdfPigDocumentTextExtractor.ExtractPreviewAsync` | returns exactly N pages on a multi-page fixture and no more; encrypted → `Encrypted`; malformed → `Malformed`; oversize → `SizeExceeded`; preview carries no whole-document text |
| `FallbackDocumentTextExtractor` | forwards preview calls; falls back under the same conditions as `ExtractAsync` |
| `DocumentLinker` | oversized document is skipped with `TryOpenReadAsync` never called (`DidNotReceive()`); a throwing blob-open marks that document `Failed` and the batch completes; a fake extractor recording max-observed concurrency never exceeds `ExtractionConcurrency` |

**The strongest regression evidence already exists.** The linker only ever consumed pages 0 and
1, so a 2-page preview must produce byte-identical linking outcomes. The committed golden link
set (`tests/PinballWizard.Application.Tests/Fixtures/Linking/golden-link-set.captured.json`,
640 docs / 755 fan-out entries) and its 18/18 replay are the check that this refactor changed no
behaviour. Replay drift means the design is wrong, not that the baseline needs recapturing.

## Observability

One new counter, `pinwiz.linker.extraction_skipped_total`, tagged `reason`
(`size_exceeded` / `blob_missing` / `extract_failed`) — following the
`pinwiz.download.too_large_skip_total` precedent of separating permanent honest skips from
failures. The inventory entry lands in `docs/observability.md` in the same PR (documentation DoD,
not a follow-up).

## Rollout

`ExtractionConcurrency` defaults to 4 **in code**, with config override optional, so no Bicep
env-var change is required. This is deliberate: issue #651 records that image-only merges never
apply Bicep env/RBAC changes, so a design needing a new env var would ship silently without it.
The image-only post-merge deploy is sufficient here.

Note the linker job currently runs image `6623087`, which predates #827; the post-merge deploy
for this change will carry both.

## Acceptance

1. Mechanism tests green; golden-link-set replay unchanged at 18/18.
2. Post-merge `Deploy` run green.
3. Linker ACA job triggered manually: `doc_4ea5f0c438428b8b` extracts (or is honestly recorded as
   a skip with a reason), and the execution reports **Succeeded**.

Item 3 is the evidence that closes #832 — the issue's own acceptance bar is written in terms of
the nightly execution result, so tests alone do not close it.
