# Linker extraction memory — design

**Date:** 2026-08-12
**Issue:** [#832](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/832)
**Status:** implemented on branch Dev-Issue832-LinkerExtractionMemory (this plan: docs/superpowers/plans/2026-08-12-linker-extraction-memory.md) — revised twice after adversarial review (see below)

> **Revision, 2026-08-12.** Adversarial review of the first draft falsified two of its claims, and
> both corrections are load-bearing rather than cosmetic:
>
> 1. The draft cited the golden-link-set replay as proof that this refactor changes no behaviour.
>    The replay runs with `textExtractor: null`, so it cannot execute the tiers this change
>    touches. The spec now requires building the gate that was assumed to exist.
> 2. The draft put a required `ExtractPreviewAsync` on the shared `IDocumentTextExtractor`,
>    justified by compile-time enforcement across all implementations. The ADI implementation
>    cannot honor a memory bound and its preview would be unreachable, so the design moved to a
>    narrow `IDocumentPreviewExtractor`.
>
> Both errors were assertions made without opening the file that would have refuted them. Recorded
> here rather than quietly edited, because a spec's revision history is part of its evidence.
>
> **Second revision, same day.** A re-audit of the first revision (design attack + independent
> fact-falsification, run in parallel) produced four more corrections: the Linux `DeleteOnClose`
> unlink was claimed to happen at open — refuted from `SafeFileHandle.Unix.cs`, it happens at
> dispose, so crash safety now rests on ACA container-scoped storage lifetime (cited); the
> `IDocumentPreviewExtractor` DI registration was unspecified, which would have failed *silently*
> (optional resolution → extraction quietly disabled in production while every test stayed
> green); the page-text fixture's capture mechanism was understated (page text is transient —
> capturing it is a new CLI verb with blob access, and the fixture now takes an explicit
> truncated-excerpt copyright posture); and `GetSizeAsync`'s error handling is now pinned inside
> the per-document try. The ephemeral-storage budget (2 GiB at 0.5 vCPU) is now cited from
> Microsoft Learn rather than left unquantified.

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

Temp files are created under `Path.GetTempPath()` (`/tmp` in the Linux container) with
`Path.GetRandomFileName()` and `FileMode.CreateNew` + `FileOptions.DeleteOnClose`.

**What `DeleteOnClose` does and does not guarantee on Linux** (verified against .NET runtime
source, `SafeFileHandle.Unix.cs` — "Unix doesn't directly support DeleteOnClose, so we mimic it
here"): the `Unlink` happens in `SafeFileHandle.ReleaseHandle()`, i.e. **at dispose, not at
open**. The normal path is therefore clean — `await using` disposes the stream and the file is
deleted immediately. But a SIGKILL (e.g. the container OOM-killer) never runs `ReleaseHandle`,
so the file survives on disk. An earlier draft claimed the unlink happened at open, POSIX
anonymous-file style; that was wrong.

Crash safety comes from the platform instead: ACA **container-scoped storage "is temporary and
disappears when the container shuts down or restarts"** (Microsoft Learn,
*Use storage mounts in Azure Container Apps*, retrieved 2026-08-12). A process killed hard takes
its container down, and the orphaned temp file dies with it — it can never outlive the failed
execution, and the next execution starts on a fresh filesystem.

**Disk budget** (same source): total ephemeral storage at ≤0.5 vCPU is **2 GiB**. Worst case
here is `ExtractionConcurrency (4) × MaxStreamBytes (100 MB) = 400 MB` — inside the budget with
4× headroom, and the size guard in Section C fires before any stream is opened, so an over-cap
blob never reaches disk at all.

**Effect on the RAG ingestion path.** `ScrapedDocumentIngestionPipeline` reaches the same blobs
through `BlobDocumentBytesSource` → `OpenReadAsync`, so it inherits this fix and stops holding
whole documents on the heap. It continues to call the full `ExtractAsync` (all pages) — that is
intentional and correct: the indexer needs the entire document. It is not exposed to the failure
this spec fixes, because the Change-Feed worker processes one document per pipeline invocation
and so has no concurrency multiplier at the extraction layer, and it runs in a different
container envelope from the linker job's 0.5 vCPU / 1 GiB. Stated here so a reader does not have
to derive it.

**Alternative considered — blob range-streaming.** `BlobBaseClient.OpenReadAsync` returns a
`LazyLoadingReadOnlyStream`, verified against the SDK source at tag
`Azure.Storage.Blobs_12.29.1`: `CanSeek` returns `true`, `Seek` is fully implemented (reusing
the buffer in range, invalidating and re-fetching outside it), and `BlobOpenReadOptions.BufferSize`
bounds memory. It is viable and needs no disk. Rejected because PdfPig seeks widely — the xref
table is at EOF and object streams are scattered — so every out-of-buffer seek issues a fresh
range GET. That trades a known OOM for an unmeasured latency risk. A temp file makes those seeks
local and predictable.

### B. A separate `IDocumentPreviewExtractor`, returning a distinct `ExtractedPreview`

New **narrow interface** in `Application/Rag/Extraction/`, implemented only by PdfPig:

```csharp
public interface IDocumentPreviewExtractor
{
    Task<ExtractedPreview> ExtractPreviewAsync(Stream pdfStream, int pageCount, CancellationToken ct);
}
```

`ExtractedPreview` carries `Status`, the first N `ExtractedPage`s, and `Error` — deliberately
**no** whole-document `Text` and **no** `Outline`. That omission is what eliminates sink #2.

- `PdfPigDocumentTextExtractor` implements both interfaces. The preview uses
  `document.GetPage(i)` for `i in 1..min(pageCount, NumberOfPages)`. Verified against PdfPig
  source at tag `v0.1.15` (`src/UglyToad.PdfPig/Content/Pages.cs`): `GetPage` resolves a single
  page node and calls `pageFactory.Create` for that page only — construction is strictly on
  demand, with no page cache. Requesting two pages therefore parses two pages, not all of them.
- `IDocumentTextExtractor` is **unchanged**. The RAG indexing path keeps exactly the contract it
  has today.
- `DocumentLinker` depends on `IDocumentPreviewExtractor` and calls it with `pageCount: 2`,
  matching the only two page tiers it has.

**DI registration (load-bearing — do not skip).** `AddPdfDocumentTextExtractor`
(`Rag/Extraction/ServiceCollectionExtensions.cs`) must register the preview interface in **both**
branches (with and without ADI):

```csharp
services.TryAddSingleton<IDocumentPreviewExtractor>(
    sp => sp.GetRequiredService<PdfPigDocumentTextExtractor>());
```

The linker's DI factory (`Persistence/Cosmos/ServiceCollectionExtensions.cs`) resolves it with
`GetService` — optional, like `IDocumentTextExtractor` today, because scraper-only CLI mode
legitimately runs without extraction wiring. That optionality is exactly what makes a missed
registration a **silent** failure: startup would not throw, the unit tests construct fakes
directly and would stay green, and in production every page-tier document would quietly fall to
`not_in_catalog` — the OOM "fixed" by disabling the feature. Registering the preview interface
inside the same method that registers PdfPig makes "extraction module present ⇒ preview
resolvable" an invariant rather than a thing to remember. The extraction-wired linker path gets
one integration test asserting that a container built with `AddPdfDocumentTextExtractor` resolves
a non-null `IDocumentPreviewExtractor`.

`ExtractedPreview.Status` reuses `ExtractionStatus`, and the preview path can produce exactly
four of its values: `Success`, `Encrypted`, `Malformed`, `SizeExceeded`. `OcrRequired` (heuristic
deliberately skipped) and `OcrFailed` (no ADI in the preview path) never appear — the
`ExtractPreviewAsync` doc comment states this so a reader doesn't have to reason about zombie
states. A dedicated narrower enum was considered and rejected: two status enums over one parser
is more surface than the two unreachable values cost.

The preview path deliberately does **not** apply the `OcrRequiredCharFloor` heuristic. That
classification exists for the indexing path; a linker preview yielding empty text simply produces
no evidence and the tier declines, which is the honest outcome.

**Why a separate interface rather than a method on `IDocumentTextExtractor`.** The first draft of
this design put a *required* `ExtractPreviewAsync` on the shared interface, arguing that
compiler-enforcement across all three implementations prevents one from silently ignoring the
limit and continuing to OOM. Review falsified that argument on two counts:

1. **ADI cannot honor a memory bound.** `AzureDocumentIntelligenceExtractor.ReadToBytesAsync`
   (`AzureDocumentIntelligenceExtractor.cs:96-104`) does `CopyToAsync` into a `MemoryStream` and
   then `ToArray()` — **two** full copies of the blob — before the request is sent. ADI's page
   range limits what the service *analyses*; it cannot limit what the client materializes. A
   required method that one implementation can only satisfy dishonestly is not enforcement.
2. **It would be dead code.** `FallbackDocumentTextExtractor` delegates to ADI only when the
   primary returns `OcrRequired` (`FallbackDocumentTextExtractor.cs:54`), and the preview path
   deliberately never returns `OcrRequired`. An ADI preview implementation would therefore be
   unreachable on every real execution path — dead code in a repo whose bar is that a senior
   architect can trace any subsystem in five minutes.

A narrow interface implemented by the one type that can honor it is the honest expression:
nothing unreachable, nothing unhonourable, and the RAG contract untouched. The cost is a second
abstraction over the same parser, and the loss of a future OCR-backed preview — which is Tier 5,
already deferred. If Tier 5 ever lands, ADI can implement `IDocumentPreviewExtractor` then, when
there is a real caller.

`ExtractedPreview` remains a distinct type rather than a flagged `ExtractedDocument`: because it
is not an `ExtractedDocument`, a truncated parse is *type-incompatible* with the chunking and
indexing path, so a partial document cannot be indexed as if complete. The bad state is
unrepresentable rather than merely discouraged, which is the posture
`.claude/rules/sdd-artifact-hygiene.md` argues for.

### C. Size guard moves upstream

`DocumentLinker.TryExtractDocumentAsync` calls the already-present
`IDocumentBlobStore.GetSizeAsync` (a blob-properties call, no body download) and returns a
`SizeExceeded` skip **before** opening anything — so an oversized blob is never transferred to
container disk. This matters because ACA ephemeral storage at this job size is finite and blobs
may be up to the 500 MB download cap.

The extractor keeps its `.Length` check as defence in depth; it is now meaningful, because the
stream is disk-backed by the time it runs. `PdfExtractionOptions.MaxStreamBytes` remains the
single source of the threshold — one value, two enforcement points, no duplicated constant.

**`GetSizeAsync` lives inside the same `try` as the open** (Section E's reasoning applies to it
identically — it is a network call whose transient failure must degrade per-document, not escape
to the batch handler). A `null` return (blob absent, 404) maps to the same `blob_missing` skip as
a `null` from `TryOpenReadAsync`; a size over the cap maps to the `size_exceeded` skip; only a
thrown exception marks the document `Failed`.

**How the linker gets the value — as a primitive, not an `IOptions<T>`.** `DocumentLinker`'s
constructor already takes `int cosmosWriteConcurrency = 20` as a plain parameter resolved at DI
registration (`DocumentLinker.cs:98`, wired in `Persistence/Cosmos/ServiceCollectionExtensions.cs`).
`maxExtractionBytes` follows that established precedent: DI plucks `options.Value.MaxStreamBytes`
and passes a `long`. This keeps `PdfExtractionOptions` as the single source of the constant while
avoiding a direct dependency from a 9-parameter orchestration class onto an extraction-config
type. Injecting `IOptions<PdfExtractionOptions>` into `DocumentLinker` is explicitly **not** the
intended implementation.

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
| `DocumentLinker` | oversized document is skipped with `TryOpenReadAsync` never called (`DidNotReceive()`); a throwing blob-open marks that document `Failed` and the batch completes; extraction concurrency never exceeds `ExtractionConcurrency` (see the gate note below) |

**The concurrency test must not be able to pass vacuously.** If the fake extractor returns
synchronously, workers complete before their peers start and max-observed concurrency may never
exceed 1 — with or without the semaphore. A test that passes when the fix is reverted proves
nothing (`no-masking-skips.md`). The fake must therefore hold a gate: increment a counter, record
the max, `await` a `SemaphoreSlim` the test controls, decrement on release. The test asserts the
max **while the workers are still parked**, then releases. Written that way it fails if the
production gate is removed; written the obvious way it does not.

### The regression evidence this change needs does not exist yet — build it

An earlier draft of this spec claimed the committed golden link set and its replay were "the check
that this refactor changed no behaviour." **That claim was false.**
`GoldenLinkSetReplayTests.MakeLinkerAsync` constructs the linker with
`textExtractor: null, blobStore: null` (`GoldenLinkSetReplayTests.cs:100-105`), so tiers 3 and 4 —
the only consumers of extraction — never execute during replay. The replay passes identically
whether this change is correct, broken, or absent. Citing it would have been the exact defect
`docs/learning-from-failure.md` records for PR #752: a gate that reads as proof and verifies
nothing.

The existing replay still earns its place as a guard that **slug- and filename-tier behaviour is
unchanged**, and it must stay green. It simply cannot speak to the page tiers.

So this PR adds the missing gate: a **page-text replay fixture**. Each entry carries the
document's recorded page-1 and page-2 text alongside its expected machine binding; the test wires
a fake `IDocumentPreviewExtractor` that replays those excerpts, so tiers 3 and 4 execute offline
with no Azure dependency. Its assertions are the same no-misattribution check the existing replay
makes.

**Capturing it is new tooling, not the existing Cosmos query.** Page text exists only transiently
inside extraction — it is never written to Cosmos, and the existing `--capture-golden-set`
(a Cosmos join, per `Fixtures/Linking/CAPTURE.md`) cannot produce it. The capture path is a new
CLI verb (or an extension flag on the existing one) that: selects documents whose recorded
resolution strategy is a page tier, opens each blob via `IDocumentBlobStore`, runs
`ExtractPreviewAsync(pageCount: 2)`, and serializes the excerpts with the expected machine ID.
It needs live blob access, so capture runs from a correctly-wired terminal like every other
capture — the *replay* is what becomes offline. Budget this as real work in the plan; it is the
largest single piece of the testing section.

**Copyright posture (explicit, so it isn't discovered mid-PR):** the fixture stores **truncated
excerpts, not whole pages** — the first ~1,000 normalized characters per page, which is where a
manual's cover-page title lives and is all the resolver consumes evidence from. Small excerpts of
published manuals, used solely as test fixtures for interoperability, in a repo that already
commits titles, link text and URLs from the same sources, is a deliberately conservative fair-use
posture. The capture tool must verify at capture time that each truncated excerpt still resolves
to the same machine as the full page did, and keep more text for any entry where truncation
changes the outcome — otherwise the fixture would silently encode a weaker resolver input than
production sees.

This closes a second, pre-existing gap found in the same review. The 23 American Pinball entries
added by the `#834` re-capture contribute **zero** regression protection today: AP documents have
a null `GameSlug`, so with no extractor they resolve `NotInCatalog` → `needs_review` → 
non-blocking. The fixture grew from 631 to 640 documents while the assurance did not move. A
page-text replay is what makes those entries load-bearing, and it is required work in this PR
rather than a follow-up, because it is the only thing that can protect the change this PR makes.

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

1. Mechanism tests green; existing golden-link-set replay still green (slug/filename tiers
   unchanged); **new page-text replay green**, and demonstrably red when the page-limited preview
   is reverted — a gate that cannot fail is not a gate.
2. Post-merge `Deploy` run green.
3. Linker ACA job triggered manually: `doc_4ea5f0c438428b8b` extracts (or is honestly recorded as
   a skip with a reason), and the execution reports **Succeeded**.

Item 3 is the evidence that closes #832 — the issue's own acceptance bar is written in terms of
the nightly execution result, so tests alone do not close it.
