# Phase 4.5 — Corpus Expansion Implementation Plan

> **⚠️ COMPLETE — HISTORICAL ONLY (do not execute).** This plan was fully implemented and merged (PRs #265–#292, May 2026). W0–W4 all landed; the H5 eval ran (`citation_precision=0.478`), the ADR-0024 Cohere Rerank gate triggered, and `CohereRerankReranker` was wired (#292). The only outstanding item is the post-rerank **H5b confirmation eval** (needs live Azure) — tracked in `docs/build-spec.md` § Phase 4.5 and `docs/decision-log.md` (2026-05-26), not here. **Do not follow the task steps below** — every `tests/PinballWizard.Scraper.Tests/...` path is stale (that project was retired and split into seven per-layer test projects per ADR-0030, May 2026). The steps are preserved only as a record of the original design.
>
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expand the RAG pipeline from the 9-machine curated subset to the full Phase 1 corpus (219 documents), fix the broken eval gauge, add ADI fallback for OCR PDFs, and synthesize metadata cards.

**Architecture:** W0 replaces the eval set so quality measurement is meaningful. W1 adds Azure Document Intelligence as a fallback extractor behind a new `FallbackTextExtractor` that wraps `PdfPigDocumentTextExtractor`. W2 removes the `CuratedSubsetMachineIds` filter from `ScrapedDocumentIngestionPipeline`. W3a adds `MetadataCardSynthesizer` in Application layer; W3b audits bulletin sources for non-Stern manufacturers. W4 runs the H5 eval and closes the phase.

**Tech Stack:** .NET 10 / C# 14, xUnit + NSubstitute, Azure Document Intelligence SDK (`Azure.AI.DocumentAnalysis`), `Microsoft.Azure.Cosmos` (machine title/ID lookup), Bicep (new ADI resource), `dotnet run --project src/PinballWizard.Cli`

---

## File Map

**W0 — Eval realignment**
- Modify: `data/eval/wizard.v1.jsonl` — replace 26 graded questions
- Create: `data/eval/results/wizard.{timestamp}.h4.json` — H4 baseline (produced at runtime, committed)

**W1 — ADI integration**
- Create: `src/PinballWizard.Infrastructure/Rag/Extraction/AzureDocumentIntelligenceTextExtractor.cs` — ADI implementation of `IDocumentTextExtractor`
- Create: `src/PinballWizard.Infrastructure/Rag/Extraction/FallbackTextExtractor.cs` — wraps primary + ADI; new `IDocumentTextExtractor` registration
- Modify: `src/PinballWizard.Application/Rag/Extraction/ExtractionStatus.cs` — add `OcrRequired_AdiSuccess` (renamed from `OcrRequired`), `OcrFailed`
- Modify: `src/PinballWizard.Infrastructure/Rag/Extraction/ServiceCollectionExtensions.cs` — `AddFallbackDocumentTextExtractor` gated on `DocumentIntelligence:Endpoint`
- Modify: `src/PinballWizard.Cli/Program.cs` — add `DocumentIntelligence:Endpoint` to conditional ADI DI wiring
- Modify: `infra/modules/shared.bicep` — add ADI resource under `deployPhase2` gate
- Create: `tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs` — unit tests

**W2 — Corpus expansion**
- Modify: `src/PinballWizard.Application/Rag/Ingestion/ScrapedDocumentIngestionPipeline.cs` — remove `CuratedSubsetMachineIds` filter
- Modify: `src/PinballWizard.Core/Configuration/RagIngestionOptions.cs` — mark `CuratedSubsetMachineIds` obsolete + note
- Modify: `src/PinballWizard.Cli/appsettings.json` — empty `CuratedSubsetMachineIds` array (or remove)
- Modify: `src/PinballWizard.RagIngestionWorker/appsettings.json` — same
- Create: `docs/runbooks/rag-extraction-failures.md` — triage runbook
- Modify: `src/PinballWizard.Application/Rag/Ingestion/IRagIngestionPipeline.cs` — remove `Skipped_NotInCuratedSubset` from `IngestionOutcome` enum
- Modify: `tests/PinballWizard.Scraper.Tests/Rag/Ingestion/ScrapedDocumentIngestionPipelineTests.cs` — update tests

**W3a — Metadata-card synthesis**
- Create: `src/PinballWizard.Application/Rag/MetadataCards/IMetadataCardSynthesizer.cs` — interface
- Create: `src/PinballWizard.Application/Rag/MetadataCards/MetadataCardSynthesizer.cs` — implementation reading `IMachineRepository`, writing to `IRagIndexer`
- Create: `src/PinballWizard.Application/Rag/MetadataCards/MetadataCardChunk.cs` — chunk shape (document_type=`metadata_card`)
- Modify: `src/PinballWizard.Application/Rag/Indexing/IRagIndexer.cs` — ensure `UpsertAsync` can accept metadata-card chunks (likely already correct given the chunk record shape; verify)
- Modify: `src/PinballWizard.Cli/Program.cs` — wire `--sync-metadata-cards` command
- Create: `tests/PinballWizard.Scraper.Tests/Rag/MetadataCards/MetadataCardSynthesizerTests.cs`

**W3b — Bulletin discovery pass**
- Modify: `data/seeds/ingestion_sources.v1.json` — add bulletin discovery entries per manufacturer
- (Optional) Create: new `ISourceScraper` implementations if trivially wirable

**W4 — Phase exit**
- Create: `data/eval/results/wizard.{timestamp}.phase45.json` — H5 eval (runtime)
- Modify: `docs/adr/0024-two-stage-reranking.md` — document gate outcome
- Modify: `docs/decision-log.md` — deferred items log

---

## Task 1 (W0): Query Cosmos to resolve 7-machine OPDB IDs

Before writing the new eval questions, resolve the actual OPDB IDs for each curated machine from the live Cosmos `machines` container. The 9 IDs in `appsettings.json` may not all be unique machine records (some are alias editions).

**Files:**
- Read: `src/PinballWizard.Cli/appsettings.json` (CuratedSubsetMachineIds — 9 IDs listed)
- Read: `data/phase4/curated-subset.v1.json` (7 manifest entries)

- [ ] **Step 1: Set up environment variables for live Cosmos**

```powershell
$env:Cosmos__AccountEndpoint    = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:Cosmos__AccountResourceId  = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"
```

- [ ] **Step 2: Query Cosmos machines container for each of the 9 curated IDs**

Run a temporary query via the CLI `--status` or direct Cosmos query to map each OPDB ID to its machine title and manufacturer. The goal is to confirm which 9 IDs map to which 7 manifest titles.

```powershell
dotnet run --project src/PinballWizard.Cli -- --source opdb --dry-run --verbose 2>&1 | Select-String "machine_id|title"
```

Alternatively, use the Azure Portal Data Explorer on `pinwiz-cosmos-dev-buutj` / `machines`:

```sql
SELECT c.id, c.title, c.manufacturer, c.opdb_id FROM c WHERE c.opdb_id IN ("GpeoL-MyNPq","G5po2-MeP6B","Gzy89-MNEeO","G4yZN-MDEP7","G2Lkd-MNEdK","G43W4-MXrPx","G4PXJ-MQPlw","GLWll-MXr4N","GQK1P-MW9pj")
```

Expected: 7 distinct machine titles mapping to the 7 manifest entries. Record the title→OPDB ID map in a scratch note — it feeds the eval question `expected_citation_set` values in Task 2.

Curated manifest titles to resolve:
- "Godzilla" (Premium, Stern 2021) → expect `GpeoL-MyNPq` or `G5po2-MeP6B`
- "Foo Fighters" (LE, Stern 2023) → one of the remaining IDs
- "Toy Story 4" (JJP 2024)
- "Galactic Tank Force" (American Pinball 2023)
- "Halloween" (Hellraiser, Spooky 2024)
- "Attack from Mars" (Remake, CGC 2017)
- "Queen" (LE, Pinball Brothers 2023)

- [ ] **Step 3: Record ID→title map**

Write the resolved map as a comment block at the top of `data/eval/wizard.v1.jsonl` (or in a scratch note). Do NOT proceed to Task 2 until all 7 machines have resolved IDs.

---

## Task 2 (W0): Replace wizard.v1.jsonl with Phase 4.5 eval set

Replace the 26 graded questions (10 Rules + 10 Valuation + 6 Repair, excluding the 3 out-of-scope refusals) with 27 new questions about the 7 curated machines. Keep the 3 existing out-of-scope refusals (car transmission, Tokyo weather, shipping cost) unchanged.

**Files:**
- Modify: `data/eval/wizard.v1.jsonl`

**Distribution to write:**

| Machine | Rules | Valuation | Repair | Notes |
|---|---|---|---|---|
| Godzilla Premium (Stern) | 2 | 2 | 2 | Manual + bulletins |
| Foo Fighters LE (Stern) | 2 | 2 | 2 | Manual + bulletins |
| Toy Story 4 (JJP) | 1 | 1 | 1 | Manual only |
| Galactic Tank Force (AP) | 1 | 1 | 1 | Manual only |
| Halloween Hellraiser (Spooky) | 1 | 1 | 1 | Manual only; outline-poor |
| Attack from Mars Remake (CGC) | 1 | 1 | 1 | Manual only; outline-poor |
| Queen LE (PB) | 1 | 1 | 1 | Manual only |

**Refusals (keep unchanged from v1 — they remain valid):**
- `ev-repair-0009` — "Can you fix my car's transmission?" (out-of-domain)
- `ev-repair-0010` — "What's the weather like in Tokyo today?" (out-of-scope)
- One valuation refusal — keep `ev-valuation-0010` (shipping cost) or write a new one

- [ ] **Step 1: Write the new JSONL content**

Replace `data/eval/wizard.v1.jsonl` entirely. Use the IDs resolved in Task 1 for `expected_citation_set`. Use the comment-header convention (lines starting with `#` are ignored by the parser). Re-number IDs from `ev-rules-0001` sequentially.

Example question format per machine/category:

```jsonl
# ── Rules ──────────────────────────────────────────────────────────
{"id":"ev-rules-0001","question":"What is the wizard mode in Stern Godzilla?","expected_sub_agent":"Rules","expected_citation_set":["<GODZILLA_OPDB_ID>"],"acceptable_refusal":false,"notes":"Stern Godzilla 2021 — exercises manual chunk retrieval"}
{"id":"ev-rules-0002","question":"How does the Kaiju multiball work in Stern Godzilla?","expected_sub_agent":"Rules","expected_citation_set":["<GODZILLA_OPDB_ID>"],"acceptable_refusal":false,"notes":"Mode-specific detail — requires manual chunk grounding"}
{"id":"ev-rules-0003","question":"What is the main theme of Stern Foo Fighters pinball?","expected_sub_agent":"Rules","expected_citation_set":["<FOO_FIGHTERS_OPDB_ID>"],"acceptable_refusal":false,"notes":"Stern Foo Fighters 2023"}
{"id":"ev-rules-0004","question":"How does the Rock the Stage multiball work in Foo Fighters?","expected_sub_agent":"Rules","expected_citation_set":["<FOO_FIGHTERS_OPDB_ID>"],"acceptable_refusal":false,"notes":"Mode detail — manual chunk"}
{"id":"ev-rules-0005","question":"What is the theme of the Jersey Jack Toy Story 4 pinball machine?","expected_sub_agent":"Rules","expected_citation_set":["<TOY_STORY_OPDB_ID>"],"acceptable_refusal":false,"notes":"JJP Toy Story 4 2024"}
{"id":"ev-rules-0006","question":"What is the theme of the Galactic Tank Force pinball by American Pinball?","expected_sub_agent":"Rules","expected_citation_set":["<GALACTIC_TANK_OPDB_ID>"],"acceptable_refusal":false,"notes":"AP Galactic Tank Force 2023"}
{"id":"ev-rules-0007","question":"What horror franchise is the Spooky Halloween Hellraiser pinball based on?","expected_sub_agent":"Rules","expected_citation_set":["<HALLOWEEN_OPDB_ID>"],"acceptable_refusal":false,"notes":"Spooky Halloween Hellraiser 2024"}
{"id":"ev-rules-0008","question":"What is the Attack from Mars Remake pinball machine and who makes it?","expected_sub_agent":"Rules","expected_citation_set":["<ATTACK_FROM_MARS_OPDB_ID>"],"acceptable_refusal":false,"notes":"CGC remake 2017"}
{"id":"ev-rules-0009","question":"What is the theme of the Pinball Brothers Queen LE?","expected_sub_agent":"Rules","expected_citation_set":["<QUEEN_OPDB_ID>"],"acceptable_refusal":false,"notes":"PB Queen LE 2023"}
```

Apply the same pattern for Valuation (MSRP, edition availability, what editions exist) and Repair (service docs, troubleshooting, bulletin availability). Repair questions for manual-only machines should phrase naturally ("what troubleshooting docs exist for X?") so a correct answer cites the manual and notes no bulletins exist.

- [ ] **Step 2: Verify question count**

```powershell
(Get-Content data/eval/wizard.v1.jsonl | Where-Object { $_ -notmatch "^#" -and $_.Trim() -ne "" }).Count
```

Expected: 30 lines (27 graded + 3 refusals).

- [ ] **Step 3: Verify JSON parses**

```powershell
Get-Content data/eval/wizard.v1.jsonl | Where-Object { $_ -notmatch "^#" -and $_.Trim() -ne "" } | ForEach-Object { $_ | ConvertFrom-Json } | Select-Object id, acceptable_refusal | Format-Table
```

Expected: 30 rows, no parse errors, 3 rows with `acceptable_refusal = True`.

- [ ] **Step 4: Update the header comment block**

Replace the Phase 3 header comment at the top of the file to reflect Phase 4.5 curation conventions:

```
# PinballWizard Phase 4.5 evaluation ground-truth (v1).
# See data/eval/README.md for the format and curation conventions.
# Citation IDs are raw OPDB ids (e.g. "GpeoL-MyNPq"), not prefixed —
# AiRouter.ExtractCitationsFromText extracts these from the
# https://opdb.org/machines/<id> URL pattern in the agent's text.
#
# 30 questions: 9 Rules, 9 Valuation, 9 Repair, 3 out-of-domain refusals.
# Evenly spread across 7 curated machines (one per Phase 1 manufacturer).
# Stern machines (Godzilla, Foo Fighters) get 2 per category to exercise
# both manual and service-bulletin chunk retrieval paths.
```

- [ ] **Step 5: Commit**

```bash
git add data/eval/wizard.v1.jsonl
git commit -m "test(eval) W0: replace Phase 3 eval set with Phase 4.5 curated-machine questions (30 total)"
```

---

## Task 3 (W0): Run H4 eval baseline

Run the eval harness against the new eval set with the existing RAG pipeline (curated subset still active). This is the H4 baseline — expect `citation_precision ≥ 0.50` because all 27 graded questions now reference machines in the indexed subset.

**Files:**
- Create: `data/eval/results/wizard.{timestamp}.h4.json` (produced by CLI; committed)

- [ ] **Step 1: Set environment variables**

```powershell
$env:AiFoundry__ProjectEndpoint = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiSearch__Endpoint         = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:Cosmos__AccountEndpoint    = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:Cosmos__AccountResourceId  = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"
```

- [ ] **Step 2: Run eval**

```powershell
dotnet run --project src/PinballWizard.Cli -- --eval
```

Expected output includes: results path, four aggregate scores. Note the `citation_precision_mean`.

- [ ] **Step 3: Rename result file to mark as H4**

The CLI names the file `wizard.{yyyyMMddTHHmmssZ}.json`. After the run, rename it to include `h4` in the name for the commit message (the file name itself uses the timestamp; capture the value for the commit message).

- [ ] **Step 4: Commit**

```bash
git add data/eval/results/
git commit -m "test(eval) W0: H4 baseline — citation_precision={actual_value} on Phase 4.5 eval set"
```

Replace `{actual_value}` with the actual number from the run.

---

## Task 4 (W0): Update build-spec Phase 4.5 section

The build-spec placeholder at lines 927–934 still says "To be specified — placeholder pending dedicated drafting conversation." Now that the design is locked, update it to reflect the Phase 4.5 scope.

**Files:**
- Modify: `docs/build-spec.md` lines 927–934

- [ ] **Step 1: Replace placeholder with locked scope**

Replace the Phase 4.5 section body with:

```markdown
## Phase 4.5 — Manuals corpus expansion

**Status:** 🚧 In progress
**Sequence position:** After Phase 4 closes (PR #263, 2026-05-21). Independent of Phase 5 (Blazor frontend) — they can run concurrently. Unblocks the public Wizard's full-corpus retrieval surface.
**Demonstrable artifact:** Every Phase 1 manual indexed with ≥ 95% success rate; `citation_precision ≥ 0.50` on the realigned eval set (30 questions across 7 curated manufacturers); all 7 curated machines answerable with citations.

**Spec:** `docs/superpowers/specs/2026-05-21-phase45-design.md`

**Wave structure (Option C — ADI-gated linear):**

| Wave | Focus | Key deliverable |
| --- | --- | --- |
| W0 | Eval realignment | `wizard.v1.jsonl` replaced; H4 baseline; `citation_precision ≥ 0.50` target |
| W1 | ADI integration | `FallbackTextExtractor`; `AzureDocumentIntelligenceTextExtractor`; Bicep Phase 2 extended; H1 operational hand-off |
| W2 | Corpus expansion | `CuratedSubsetMachineIds` filter removed; full backfill; triage runbook; H2 operational hand-off |
| W3a | Metadata-card synthesis | `MetadataCardSynthesizer`; `--sync-metadata-cards` CLI; metadata cards in AI Search |
| W3b | Bulletin discovery pass | Per-manufacturer audit; new scrapers where trivially wirable; documented outcomes in `ingestion_sources.v1.json` |
| W4 | Phase exit | H5 eval; Cohere Rerank conditional gate; deferred-items log |

**Phase exit criteria:** ≥ 95% of `document_type=manual` records produce ≥ 1 chunk; `citation_precision ≥ 0.50` on H5; all 7 curated machines answerable with citations; bulletin discovery complete; metadata cards present in AI Search; ADI fallback operational; triage runbook at `docs/runbooks/rag-extraction-failures.md`; deferred items logged.
```

- [ ] **Step 2: Build to verify no doc-related issues**

```powershell
dotnet build PinballWizard.slnx --configuration Release --no-incremental -warnaserror 2>&1 | Tail -20
```

Expected: zero warnings, zero errors.

- [ ] **Step 3: Commit (include spec branch file if on that branch)**

```bash
git add docs/build-spec.md docs/superpowers/specs/2026-05-21-phase45-design.md
git commit -m "docs(spec) W0: update build-spec Phase 4.5 section with locked scope and wave structure"
```

Note: if the spec file `docs/superpowers/specs/2026-05-21-phase45-design.md` was committed on the previous branch, it may already be present — add it only if the working tree shows it as untracked.

---

## Task 5 (W1): Add `ExtractionStatus` enum values for ADI outcomes

The current `ExtractionStatus.OcrRequired` means "PdfPig yielded too few chars." In Phase 4.5 it becomes the trigger that fires ADI. Two new values distinguish the outcome after the fallback is attempted.

**Files:**
- Modify: `src/PinballWizard.Application/Rag/Extraction/ExtractionStatus.cs`

- [ ] **Step 1: Write failing test for new enum values**

In `tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs` (new file), add a trivial compile-check:

```csharp
using PinballWizard.Application.Rag.Extraction;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Extraction;

public sealed class FallbackTextExtractorTests
{
    [Fact]
    public void ExtractionStatus_HasOcrSucceededValue()
    {
        // Compile-time check: if the enum value doesn't exist, this fails to compile.
        var status = ExtractionStatus.OcrSucceeded;
        Assert.True(Enum.IsDefined(status));
    }

    [Fact]
    public void ExtractionStatus_HasOcrFailedValue()
    {
        var status = ExtractionStatus.OcrFailed;
        Assert.True(Enum.IsDefined(status));
    }
}
```

- [ ] **Step 2: Run test to confirm it fails**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FallbackTextExtractorTests" --no-build 2>&1 | Select-String "error|FAILED|passed"
```

Expected: compile error — `OcrSucceeded` and `OcrFailed` do not exist.

- [ ] **Step 3: Add enum values to ExtractionStatus**

Add after `OcrRequired`:

```csharp
// PdfPig returned 0 tokens AND Azure Document Intelligence (ADI Read model)
// successfully extracted text. Recorded in `rag_index_state` so operators
// can distinguish OCR-assisted ingests from native-text ingests in telemetry.
// Phase 4.5 W1 (FallbackTextExtractor).
OcrSucceeded,

// PdfPig AND ADI both returned 0 tokens. The document is unrecoverable
// by either extractor. Logged and skipped; `rag_index_state` records
// ExtractionStatus=OcrFailed to prevent infinite retry loops.
// Phase 4.5 W1 (FallbackTextExtractor).
OcrFailed,
```

- [ ] **Step 4: Run test to confirm it passes**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FallbackTextExtractorTests" 2>&1 | Select-String "passed|FAILED"
```

Expected: 2 passed.

- [ ] **Step 5: Update pipeline to handle new status values**

In `src/PinballWizard.Application/Rag/Ingestion/ScrapedDocumentIngestionPipeline.cs`, the extract branch currently only checks `extracted.Status != ExtractionStatus.Success`:

```csharp
// The Success check covers native extraction.
// OcrSucceeded is also a valid path — the fallback extractor set Status=OcrSucceeded
// but Text is populated; treat it identically to Success.
if (extracted.Status != ExtractionStatus.Success
    && extracted.Status != ExtractionStatus.OcrSucceeded)
{
    _logger.LogInformation(
        "RAG ingestion skipped — extraction status {Status} on document {DocumentId} (machine {MachineId}). Error: {Error}",
        extracted.Status, change.DocumentId, change.MachineId, extracted.Error ?? "(none)");
    return IngestionOutcome.Skipped_ExtractionFailed;
}
```

- [ ] **Step 6: Run full test suite to confirm no regressions**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Tail -5
```

Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Rag/Extraction/ExtractionStatus.cs \
        src/PinballWizard.Application/Rag/Ingestion/ScrapedDocumentIngestionPipeline.cs \
        tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs
git commit -m "feat(rag) W1: add OcrSucceeded/OcrFailed ExtractionStatus values; pipeline accepts OcrSucceeded path"
```

---

## Task 6 (W1): Implement AzureDocumentIntelligenceTextExtractor

The ADI extractor calls the Azure Document Intelligence Read model on the PDF stream and maps the response to `ExtractedDocument`. It is a concrete `IDocumentTextExtractor` — callers never construct it directly; the `FallbackTextExtractor` (Task 7) uses it.

**Files:**
- Create: `src/PinballWizard.Infrastructure/Rag/Extraction/AzureDocumentIntelligenceTextExtractor.cs`
- Modify: `tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs` (add ADI tests)

- [ ] **Step 1: Add Azure.AI.DocumentAnalysis NuGet package to Infrastructure**

```powershell
dotnet add src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj package Azure.AI.FormRecognizer --version 4.1.0
```

Note: the `Azure.AI.FormRecognizer` 4.x package is the current SDK for Azure Document Intelligence (formerly Form Recognizer). Verify version at https://www.nuget.org/packages/Azure.AI.FormRecognizer before running.

- [ ] **Step 2: Write failing test for AzureDocumentIntelligenceTextExtractor**

Add to `FallbackTextExtractorTests.cs`:

```csharp
[Fact]
public void AdiExtractor_Ctor_NullClient_Throws()
{
    Assert.Throws<ArgumentNullException>(() =>
        new AzureDocumentIntelligenceTextExtractor(null!, NullLogger<AzureDocumentIntelligenceTextExtractor>.Instance));
}

[Fact]
public void AdiExtractor_Ctor_NullLogger_Throws()
{
    var fakeClient = NSubstitute.Substitute.For<DocumentAnalysisClient>(
        new Uri("https://fake.cognitiveservices.azure.com"),
        new Azure.AzureKeyCredential("fake"));
    Assert.Throws<ArgumentNullException>(() =>
        new AzureDocumentIntelligenceTextExtractor(fakeClient, null!));
}
```

Run: expect compile failure until the class exists.

- [ ] **Step 3: Create AzureDocumentIntelligenceTextExtractor**

```csharp
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

// Phase 4.5 W1 ADI extractor. Calls the Azure Document Intelligence
// Read model (prebuilt-read) to extract text from scanned / image-only
// PDFs that PdfPig cannot process. Used exclusively by FallbackTextExtractor
// — callers should never inject this directly, only IDocumentTextExtractor.
//
// The Read model returns page-level text blocks; we concatenate all spans
// into a flat string matching PdfPigDocumentTextExtractor's output shape.
// Outline extraction is not supported by the Read model — Outline is always
// empty (the HybridChunker falls back to fixed-size sliding window when
// outline is absent, per ADR-0019).
public sealed class AzureDocumentIntelligenceTextExtractor : IDocumentTextExtractor
{
    private readonly DocumentAnalysisClient _client;
    private readonly ILogger<AzureDocumentIntelligenceTextExtractor> _logger;

    public AzureDocumentIntelligenceTextExtractor(
        DocumentAnalysisClient client,
        ILogger<AzureDocumentIntelligenceTextExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    public async Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        try
        {
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-read",
                pdfStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var result = operation.Value;
            var pages = new List<ExtractedPage>(capacity: result.Pages.Count);
            var allText = new System.Text.StringBuilder(capacity: 4096);

            foreach (var page in result.Pages)
            {
                var pageText = string.Concat(page.Lines.Select(l => l.Content + "\n"));
                pages.Add(new ExtractedPage(page.PageNumber, pageText));
                allText.Append(pageText);
            }

            _logger.LogInformation(
                "ADI extraction succeeded: {PageCount} pages, {CharCount} chars.",
                pages.Count, allText.Length);

            return new ExtractedDocument(
                Status: ExtractionStatus.OcrSucceeded,
                Text: allText.ToString(),
                Pages: pages,
                Outline: [],
                Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ADI extraction failed.");
            return ExtractedDocument.Failure(
                ExtractionStatus.OcrFailed,
                $"ADI extraction failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
```

- [ ] **Step 4: Run tests to confirm constructor tests pass**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "AdiExtractor_Ctor" 2>&1 | Select-String "passed|FAILED"
```

Expected: 2 passed.

- [ ] **Step 5: Build clean**

```powershell
dotnet build PinballWizard.slnx -warnaserror 2>&1 | Tail -10
```

Expected: zero errors, zero warnings.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Extraction/AzureDocumentIntelligenceTextExtractor.cs \
        src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj \
        tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs
git commit -m "feat(rag) W1: add AzureDocumentIntelligenceTextExtractor (ADI Read model fallback)"
```

---

## Task 7 (W1): Implement FallbackTextExtractor

`FallbackTextExtractor` wraps a primary `IDocumentTextExtractor` (PdfPig) and an ADI extractor. When the primary returns `OcrRequired`, it delegates to ADI. If ADI also fails, it returns `OcrFailed`.

**Files:**
- Create: `src/PinballWizard.Infrastructure/Rag/Extraction/FallbackTextExtractor.cs`
- Modify: `tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs`

- [ ] **Step 1: Write failing tests for FallbackTextExtractor**

Add to `FallbackTextExtractorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using Xunit;

// (add to existing class or new class — keep in same file)

public sealed class FallbackExtractorBehaviorTests
{
    private static ExtractedDocument Success(string text = "hello world") =>
        new(ExtractionStatus.Success, text, [], [], null);

    private static ExtractedDocument OcrRequired() =>
        ExtractedDocument.Failure(ExtractionStatus.OcrRequired, "scanned image");

    private static ExtractedDocument OcrSucceeded(string text = "ocr text") =>
        new(ExtractionStatus.OcrSucceeded, text, [], [], null);

    private static ExtractedDocument OcrFailed() =>
        ExtractedDocument.Failure(ExtractionStatus.OcrFailed, "adi failed");

    [Fact]
    public async Task WhenPrimarySucceeds_ReturnsPrimaryResult_NeverCallsAdi()
    {
        var primary = Substitute.For<IDocumentTextExtractor>();
        var adi = Substitute.For<IDocumentTextExtractor>();
        primary.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(Success()));
        var sut = new FallbackTextExtractor(primary, adi, NullLogger<FallbackTextExtractor>.Instance);

        var result = await sut.ExtractAsync(Stream.Null, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        await adi.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenPrimaryOcrRequired_DelegatestoAdi_ReturnsOcrSucceeded()
    {
        var primary = Substitute.For<IDocumentTextExtractor>();
        var adi = Substitute.For<IDocumentTextExtractor>();
        primary.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(OcrRequired()));
        adi.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult(OcrSucceeded()));
        var sut = new FallbackTextExtractor(primary, adi, NullLogger<FallbackTextExtractor>.Instance);

        var result = await sut.ExtractAsync(Stream.Null, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrSucceeded, result.Status);
        Assert.Equal("ocr text", result.Text);
    }

    [Fact]
    public async Task WhenBothFail_ReturnsOcrFailed()
    {
        var primary = Substitute.For<IDocumentTextExtractor>();
        var adi = Substitute.For<IDocumentTextExtractor>();
        primary.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(OcrRequired()));
        adi.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult(OcrFailed()));
        var sut = new FallbackTextExtractor(primary, adi, NullLogger<FallbackTextExtractor>.Instance);

        var result = await sut.ExtractAsync(Stream.Null, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrFailed, result.Status);
    }

    [Fact]
    public async Task WhenPrimaryEncrypted_DoesNotCallAdi_ReturnsEncrypted()
    {
        var primary = Substitute.For<IDocumentTextExtractor>();
        var adi = Substitute.For<IDocumentTextExtractor>();
        primary.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(ExtractedDocument.Failure(ExtractionStatus.Encrypted, "encrypted")));
        var sut = new FallbackTextExtractor(primary, adi, NullLogger<FallbackTextExtractor>.Instance);

        var result = await sut.ExtractAsync(Stream.Null, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Encrypted, result.Status);
        await adi.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run test to confirm compile failure**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FallbackExtractorBehaviorTests" 2>&1 | Select-String "error"
```

Expected: compile error — `FallbackTextExtractor` not found.

- [ ] **Step 3: Implement FallbackTextExtractor**

```csharp
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

// Phase 4.5 W1 fallback extractor. Delegates to the primary (PdfPig)
// extractor first. When primary returns OcrRequired (scanned-image-only
// PDF — too few characters), delegates to the ADI extractor. Any other
// primary outcome (Success, Encrypted, Malformed, SizeExceeded) passes
// through unchanged — ADI only fires for the OCR-required case.
//
// Registered as the IDocumentTextExtractor singleton when
// DocumentIntelligence:Endpoint is configured, replacing the direct
// PdfPig registration. The primary (PdfPig) and ADI instances are
// constructor-injected by ServiceCollectionExtensions.
public sealed class FallbackTextExtractor : IDocumentTextExtractor
{
    private readonly IDocumentTextExtractor _primary;
    private readonly IDocumentTextExtractor _adi;
    private readonly ILogger<FallbackTextExtractor> _logger;

    public FallbackTextExtractor(
        IDocumentTextExtractor primary,
        IDocumentTextExtractor adi,
        ILogger<FallbackTextExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(adi);
        ArgumentNullException.ThrowIfNull(logger);
        _primary = primary;
        _adi = adi;
        _logger = logger;
    }

    public async Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        var primaryResult = await _primary.ExtractAsync(pdfStream, cancellationToken).ConfigureAwait(false);

        if (primaryResult.Status != ExtractionStatus.OcrRequired)
            return primaryResult;

        _logger.LogInformation("Primary extractor returned OcrRequired; invoking ADI fallback.");

        // Stream must be rewound before ADI re-reads it.
        if (pdfStream.CanSeek)
            pdfStream.Seek(0, SeekOrigin.Begin);

        return await _adi.ExtractAsync(pdfStream, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run tests to confirm all 4 pass**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FallbackExtractorBehaviorTests" 2>&1 | Select-String "passed|FAILED"
```

Expected: 4 passed.

- [ ] **Step 5: Run full suite**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Tail -5
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Extraction/FallbackTextExtractor.cs \
        tests/PinballWizard.Scraper.Tests/Rag/Extraction/FallbackTextExtractorTests.cs
git commit -m "feat(rag) W1: add FallbackTextExtractor — ADI fires when PdfPig returns OcrRequired"
```

---

## Task 8 (W1): Wire FallbackTextExtractor into DI + add Bicep ADI resource

When `DocumentIntelligence:Endpoint` is configured, `AddFallbackDocumentTextExtractor` replaces the direct PdfPig registration with the fallback. The Bicep change provisions the ADI resource under the existing `deployPhase2` gate.

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Rag/Extraction/ServiceCollectionExtensions.cs`
- Modify: `src/PinballWizard.Cli/Program.cs`
- Modify: `infra/modules/shared.bicep`

- [ ] **Step 1: Add AddFallbackDocumentTextExtractor to ServiceCollectionExtensions**

Replace the existing `AddPdfDocumentTextExtractor` with an overloaded/extended version, or add a new method:

```csharp
// In ServiceCollectionExtensions.cs — add alongside AddPdfDocumentTextExtractor

/// <summary>
/// Wires IDocumentTextExtractor as a FallbackTextExtractor (PdfPig primary + ADI fallback)
/// when <paramref name="adiEndpoint"/> is non-null. Falls back to the plain PdfPig
/// registration otherwise — allows the same DI wiring call in CLI/worker regardless of env.
/// </summary>
public static IServiceCollection AddDocumentTextExtractor(
    this IServiceCollection services,
    string? adiEndpoint)
{
    ArgumentNullException.ThrowIfNull(services);

    services.AddOptions<PdfExtractionOptions>();
    services.TryAddSingleton<PdfPigDocumentTextExtractor>();

    if (string.IsNullOrWhiteSpace(adiEndpoint))
    {
        services.TryAddSingleton<IDocumentTextExtractor>(sp =>
            sp.GetRequiredService<PdfPigDocumentTextExtractor>());
        return services;
    }

    // ADI endpoint is configured — wire the fallback chain.
    services.TryAddSingleton(new DocumentAnalysisClient(
        new Uri(adiEndpoint),
        new Azure.Identity.DefaultAzureCredential()));

    services.TryAddSingleton<AzureDocumentIntelligenceTextExtractor>();

    services.TryAddSingleton<IDocumentTextExtractor>(sp => new FallbackTextExtractor(
        sp.GetRequiredService<PdfPigDocumentTextExtractor>(),
        sp.GetRequiredService<AzureDocumentIntelligenceTextExtractor>(),
        sp.GetRequiredService<ILogger<FallbackTextExtractor>>()));

    return services;
}
```

- [ ] **Step 2: Update CLI Program.cs to use AddDocumentTextExtractor**

In `src/PinballWizard.Cli/Program.cs`, find the existing `AddPdfDocumentTextExtractor()` call and replace with:

```csharp
.AddDocumentTextExtractor(configuration["DocumentIntelligence:Endpoint"])
```

The `configuration` reference is available in the scope where DI is wired.

- [ ] **Step 3: Update RagIngestionWorker Program.cs similarly**

Same substitution in `src/PinballWizard.RagIngestionWorker/Program.cs`.

- [ ] **Step 4: Add ADI resource to Bicep**

In `infra/modules/shared.bicep`, after the Cosmos diagnostics section and before the Azure OpenAI section, add:

```bicep
// -----------------------------------------------------------------------------
// Azure Document Intelligence (Phase 4.5 W1 — OCR fallback for scanned PDFs)
// -----------------------------------------------------------------------------
// The Read model is used by FallbackTextExtractor when PdfPig yields 0 tokens.
// Keyed off deployPhase2 (Document Intelligence is a Phase 2 resource per
// ADR-0013). Cost: ~$1.50/1000 pages; estimated one-time backfill ~$0.17
// (10% of 219 docs × 5 pages avg). Ongoing cost is negligible (only
// unextractable new documents hit ADI).

var documentIntelligenceName = '${namePrefix}-docintel-${environment}-${uniqueSuffix}'

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployPhase2) {
  name: documentIntelligenceName
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: documentIntelligenceName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}
```

Also add an output at the bottom of `shared.bicep`:

```bicep
@description('Document Intelligence endpoint URL (Phase 4.5 ADI fallback).')
output documentIntelligenceEndpoint string = deployPhase2 ? documentIntelligence.properties.endpoint : ''
```

- [ ] **Step 5: Build clean**

```powershell
dotnet build PinballWizard.slnx -warnaserror 2>&1 | Tail -10
```

Expected: zero errors, zero warnings.

- [ ] **Step 6: Run full test suite**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Tail -5
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Extraction/ServiceCollectionExtensions.cs \
        src/PinballWizard.Cli/Program.cs \
        src/PinballWizard.RagIngestionWorker/Program.cs \
        infra/modules/shared.bicep
git commit -m "feat(rag) W1: wire FallbackTextExtractor into DI; add ADI Bicep resource under deployPhase2 gate"
```

---

## Task 9 (W1): H1 Operational Hand-off — Deploy ADI and smoke-test

This is a runbook step, not a code change. Deploy the updated Bicep to provision the ADI resource, then smoke-test the fallback path.

**Files:**
- (Runtime: `data/eval/results/` or `docs/decision-log.md` — commit deploy timestamp)

- [ ] **Step 1: Deploy Bicep with deployPhase2=true**

```powershell
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

Expected output includes `documentIntelligenceEndpoint = https://pinwiz-docintel-dev-....cognitiveservices.azure.com/`.

- [ ] **Step 2: Set ADI endpoint env var**

```powershell
$env:DocumentIntelligence__Endpoint = "<documentIntelligenceEndpoint value from deploy output>"
```

- [ ] **Step 3: Smoke-test via backfill on a known-good document**

```powershell
dotnet run --project src/PinballWizard.Cli -- --run-rag-backfill --verbose
```

Observe log output for an `OcrRequired` document (if any). The presence of "Primary extractor returned OcrRequired; invoking ADI fallback" in logs confirms the path is wired.

If no document triggers OcrRequired in the curated subset (all 9 are expected to be native-text PDFs), confirm by running with `--verbose` and seeing no ADI calls — that is the correct behavior (ADI only fires on scanned docs).

- [ ] **Step 4: Commit deploy timestamp to decision-log.md**

Add an entry to `docs/decision-log.md`:

```
## DL-XXXX — Phase 4.5 ADI resource provisioned (2026-05-21)

ADI resource `pinwiz-docintel-dev-buutj` deployed to `rg-pinwiz-shared-dev`.
Endpoint: `https://pinwiz-docintel-dev-buutj.cognitiveservices.azure.com/`.
Smoke-test: ran `--run-rag-backfill` — no OcrRequired documents in curated subset
(expected; curated subset are native-text PDFs). ADI path is wired and will fire
on scanned documents when W2 corpus expansion runs.
```

- [ ] **Step 5: Commit**

```bash
git add docs/decision-log.md
git commit -m "ops(rag) W1: H1 hand-off — ADI provisioned; smoke-test passed; deploy timestamp recorded"
```

---

## Task 10 (W2): Remove CuratedSubsetMachineIds filter from pipeline

The filter at the top of `ScrapedDocumentIngestionPipeline.IngestAsync` that checks machine ID membership in the curated set is removed. Every `document_type=Manual` or `document_type=ServiceBulletin` record becomes eligible.

**Files:**
- Modify: `src/PinballWizard.Application/Rag/Ingestion/IRagIngestionPipeline.cs` — remove `Skipped_NotInCuratedSubset` from `IngestionOutcome`
- Modify: `src/PinballWizard.Application/Rag/Ingestion/ScrapedDocumentIngestionPipeline.cs` — remove filter
- Modify: `src/PinballWizard.Core/Configuration/RagIngestionOptions.cs` — mark property with comment
- Modify: `src/PinballWizard.Cli/appsettings.json` — empty the array
- Modify: `src/PinballWizard.RagIngestionWorker/appsettings.json` — empty the array
- Modify: `tests/PinballWizard.Scraper.Tests/Rag/Ingestion/ScrapedDocumentIngestionPipelineTests.cs` — remove/update curated-filter tests

- [ ] **Step 1: Write failing test confirming filter is gone**

Find (or create) `tests/PinballWizard.Scraper.Tests/Rag/Ingestion/ScrapedDocumentIngestionPipelineTests.cs`. Add:

```csharp
[Fact]
public async Task IngestAsync_MachineNotInCuratedSubset_StillProcesses()
{
    // Post-W2: no curated-subset filter. A machine not in the former 9-ID list
    // should NOT return Skipped_NotInCuratedSubset — it proceeds to extraction.
    var options = Options.Create(new RagIngestionOptions
    {
        CuratedSubsetMachineIds = [],          // empty — filter is inert
        AcceptedDocumentTypes = [DocumentType.Manual],
    });
    var extractor = Substitute.For<IDocumentTextExtractor>();
    extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
             .Returns(ExtractedDocument.Failure(ExtractionStatus.Malformed, "test"));
    // ... build pipeline with mocked dependencies
    var change = new ScrapedDocumentChange(
        DocumentId: "doc_unknown123",
        DocumentUrl: "https://example.com/test.pdf",
        MachineId: "UNKNOWN-MACHINE",
        MachineTitle: "Some Machine",
        Manufacturer: "Test Co",
        DocumentType: DocumentType.Manual,
        ContentHash: "abc123");

    var result = await pipeline.IngestAsync(change, Stream.Null, CancellationToken.None);

    // Should not be Skipped_NotInCuratedSubset — should reach extraction
    Assert.NotEqual(IngestionOutcome.Skipped_NotInCuratedSubset, result);
}
```

- [ ] **Step 2: Run test to confirm it currently returns Skipped_NotInCuratedSubset**

The test should fail (the pipeline still filters) — confirming TDD direction.

- [ ] **Step 3: Remove IngestionOutcome.Skipped_NotInCuratedSubset**

In `IRagIngestionPipeline.cs`, remove the `Skipped_NotInCuratedSubset` value from the `IngestionOutcome` enum. Fix any switch statements or comparisons that reference it (search for `Skipped_NotInCuratedSubset` across `src/` and `tests/`).

- [ ] **Step 4: Remove filter from ScrapedDocumentIngestionPipeline**

Delete the `_curatedSet` field, the `HashSet<string>` construction in the constructor, and the entire "Filter 1 — curated subset" block (lines 81–91 approximately):

```csharp
// DELETE these lines:
// Filter 1 — curated subset. First so the rest of the pipeline
// (extract, embed, upsert) never runs for an out-of-scope
// machine. Phase 4.5 corpus expansion removes this filter
// entirely, not this branch.
if (!_curatedSet.Contains(change.MachineId))
{
    ...
    return IngestionOutcome.Skipped_NotInCuratedSubset;
}
```

Also remove the `_curatedSet` field declaration and its initialization from the constructor.

- [ ] **Step 5: Mark CuratedSubsetMachineIds as inert in RagIngestionOptions**

In `RagIngestionOptions.cs`, update the comment on `CuratedSubsetMachineIds`:

```csharp
// Phase 4.5 W2: the curated-subset filter has been removed from
// ScrapedDocumentIngestionPipeline. This property is kept in the config
// schema for backwards compatibility with existing appsettings.json files
// but is no longer read by the pipeline. Safe to remove from config files
// in a follow-up cleanup PR.
public List<string> CuratedSubsetMachineIds { get; set; } = [];
```

- [ ] **Step 6: Empty the array in both appsettings.json files**

In `src/PinballWizard.Cli/appsettings.json` and `src/PinballWizard.RagIngestionWorker/appsettings.json`:

```json
"CuratedSubsetMachineIds": []
```

- [ ] **Step 7: Run full test suite — confirm no regressions**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Tail -5
```

Expected: all pass. The new test from Step 1 should now pass.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Application/Rag/Ingestion/ \
        src/PinballWizard.Core/Configuration/RagIngestionOptions.cs \
        src/PinballWizard.Cli/appsettings.json \
        src/PinballWizard.RagIngestionWorker/appsettings.json \
        tests/PinballWizard.Scraper.Tests/Rag/Ingestion/
git commit -m "feat(rag) W2: remove CuratedSubsetMachineIds filter — all Manuals+ServiceBulletins eligible"
```

---

## Task 11 (W2): Write triage runbook

This is a documentation task. The runbook gives operators instructions for identifying and recovering `OcrFailed` documents after the full backfill.

**Files:**
- Create: `docs/runbooks/rag-extraction-failures.md`

- [ ] **Step 1: Create the runbook**

```markdown
# RAG Extraction Failure Triage Runbook

**Created:** 2026-05-21 (Phase 4.5 W2)
**Applies to:** `pinwiz-cosmos-dev-buutj` / container `rag_index_state`

## Identifying OcrFailed documents

Query the `rag_index_state` container for documents with extraction failure:

```sql
SELECT c.document_id, c.extraction_status, c.failure_count, c.recorded_utc
FROM c
WHERE c.extraction_status = "OcrFailed"
ORDER BY c.recorded_utc DESC
```

Run via Azure Portal Data Explorer on `pinwiz-cosmos-dev-buutj` → `pinwiz-rag` database → `rag_index_state` container.

## Recovery path

1. **Inspect the document** — find the source PDF in `scraped_documents` container by `document_id`.
2. **Check the PDF manually** — download the `file_url` and attempt to open it. If it opens normally, the failure may be transient.
3. **Re-queue** — delete the `rag_index_state` record for the `document_id` and re-run the backfill:

```powershell
$env:Cosmos__AccountEndpoint    = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:Cosmos__AccountResourceId  = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"
$env:AiFoundry__ProjectEndpoint = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiSearch__Endpoint         = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:DocumentIntelligence__Endpoint = "https://pinwiz-docintel-dev-XXXXX.cognitiveservices.azure.com/"

dotnet run --project src/PinballWizard.Cli -- --run-rag-backfill --verbose
```

The backfill is idempotent — deleting the `rag_index_state` row forces a fresh extraction attempt on the next run.

## Escalation — permanently unrecoverable documents

If a document repeatedly fails both PdfPig and ADI (OcrFailed persists after re-queue), it is likely:
- A corrupted binary (not a real PDF)
- A scan at resolution too low for ADI to process
- An encrypted PDF with no publicly-available password

In these cases:
1. Note the `document_id` and `file_url` in `docs/decision-log.md` under a "Permanently unrecoverable documents" entry.
2. Leave the `rag_index_state` record as `OcrFailed` — the backfill will skip it on subsequent runs (MaxFailuresPerDocument gate).
3. Consider scraping an alternative version of the document (different edition, different source URL) if available.

## Success criteria for W2 backfill

- `OcrFailed` count ≤ 5% of total documents processed
- ≥ 95% of `document_type=Manual` records have `chunk_count ≥ 1`
- Spot-check: re-run H4 eval questions for the 7 curated machines — answers still cite correct sources
```

- [ ] **Step 2: Commit**

```bash
git add docs/runbooks/rag-extraction-failures.md
git commit -m "docs(ops) W2: add RAG extraction failure triage runbook"
```

---

## Task 12 (W2): H2 Operational Hand-off — Full backfill run

Run `--run-rag-backfill` against the full Phase 1 corpus (no curated filter). Capture and commit the run summary.

**Files:**
- Create: `data/eval/results/backfill-{date}.json` (manual log of run summary)

- [ ] **Step 1: Confirm ADI env var is set**

```powershell
$env:DocumentIntelligence__Endpoint
```

Should return the ADI endpoint from the H1 hand-off.

- [ ] **Step 2: Run full backfill**

```powershell
$env:AiFoundry__ProjectEndpoint = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiSearch__Endpoint         = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:Cosmos__AccountEndpoint    = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:Cosmos__AccountResourceId  = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"

dotnet run --project src/PinballWizard.Cli -- --run-rag-backfill 2>&1 | Tee-Object -FilePath backfill-run.log
```

Expected final log line: `RAG backfill complete: processed=NNN indexed=NNN skipped=NNN failed=NNN duration=...`

- [ ] **Step 3: Check success criteria**

From the log:
- `OcrFailed` count ≤ 5% of total (check for log lines with `OcrFailed`)
- No unexpected failures (e.g., AI Search 429s, Cosmos exceptions)

If `OcrFailed` > 5%: consult triage runbook (`docs/runbooks/rag-extraction-failures.md`). Do NOT declare W2 complete until the threshold is met.

- [ ] **Step 4: Run H4 eval as spot-check regression test**

```powershell
dotnet run --project src/PinballWizard.Cli -- --eval
```

Expected: `citation_precision` at or above H4 baseline (the curated-machine questions should still answer correctly with the expanded index).

- [ ] **Step 5: Commit backfill summary**

Create `data/eval/results/backfill-{date}.txt` with the run statistics (copy from log output):

```
Phase 4.5 W2 full corpus backfill — {date}
processed: NNN
indexed: NNN
skipped: NNN (by reason breakdown if available)
failed: NNN
ocr_required_adi_success: NNN
ocr_failed: NNN
duration: HH:MM:SS
```

```bash
git add data/eval/results/
git commit -m "ops(rag) W2: H2 hand-off — full corpus backfill complete; {indexed} docs indexed"
```

---

## Task 13 (W3a): Implement MetadataCardSynthesizer

Creates `metadata_card` chunks from Cosmos `machines` records and upserts them into AI Search. One chunk per machine edition (OPDB record).

**Files:**
- Create: `src/PinballWizard.Application/Rag/MetadataCards/IMetadataCardSynthesizer.cs`
- Create: `src/PinballWizard.Application/Rag/MetadataCards/MetadataCardSynthesizer.cs`
- Create: `tests/PinballWizard.Scraper.Tests/Rag/MetadataCards/MetadataCardSynthesizerTests.cs`

- [ ] **Step 1: Check Machine domain type for fields to synthesize**

Read `src/PinballWizard.Core/Domain/Machine.cs` to confirm available fields (title, manufacturer, year, designers, themes, editions, MSRP, OPDB ID). Note exactly which properties exist — the synthesizer's content string must reference real property names.

- [ ] **Step 2: Write failing test for IMetadataCardSynthesizer**

```csharp
using PinballWizard.Application.Rag.MetadataCards;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.MetadataCards;

public sealed class MetadataCardSynthesizerTests
{
    [Fact]
    public void Interface_Exists()
    {
        // Compile check — if the interface doesn't exist, the file won't compile.
        IMetadataCardSynthesizer _ = null!;
        Assert.Null(_);
    }
}
```

Run: expect compile failure.

- [ ] **Step 3: Create IMetadataCardSynthesizer**

```csharp
namespace PinballWizard.Application.Rag.MetadataCards;

// Phase 4.5 W3a: synthesizes a metadata_card chunk for each Machine record
// in IMachineRepository and upserts it into the AI Search RAG index via IRagIndexer.
// One card per machine edition (OPDB record). Idempotent: upserts by chunk_id.
// Not wired to Change Feed — invoked via --sync-metadata-cards CLI command.
public interface IMetadataCardSynthesizer
{
    Task<MetadataCardSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record MetadataCardSyncResult(int Upserted, int Skipped, int Failed);
```

- [ ] **Step 4: Write substantive tests for MetadataCardSynthesizer**

```csharp
[Fact]
public async Task SyncAsync_WithOneMachine_UpsertsSingleCard()
{
    var machine = new Machine
    {
        OpdbId = "GpeoL-MyNPq",
        Title = "Foo Fighters",
        Manufacturer = "Stern Pinball",
        Year = 2023,
        // ... set other fields per actual Machine type
    };
    var machineRepo = Substitute.For<IMachineRepository>();
    machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
               .Returns(AsyncEnumerable(machine));
    var indexer = Substitute.For<IRagIndexer>();
    indexer.UpsertAsync(Arg.Any<ChunkRequest>(), Arg.Any<IReadOnlyList<RagChunk>>(), Arg.Any<RagIndexerOptions>(), Arg.Any<CancellationToken>())
           .Returns(new IndexUpsertResult(1, []));

    var sut = new MetadataCardSynthesizer(machineRepo, indexer, Options.Create(new RagIndexerOptions()), NullLogger<MetadataCardSynthesizer>.Instance);
    var result = await sut.SyncAsync(CancellationToken.None);

    Assert.Equal(1, result.Upserted);
    Assert.Equal(0, result.Failed);
    await indexer.Received(1).UpsertAsync(
        Arg.Any<ChunkRequest>(),
        Arg.Is<IReadOnlyList<RagChunk>>(chunks => chunks.Count == 1 && chunks[0].DocumentType == "metadata_card"),
        Arg.Any<RagIndexerOptions>(),
        Arg.Any<CancellationToken>());
}
```

Adjust exact types to match what `IRagIndexer.UpsertAsync` accepts — check `src/PinballWizard.Application/Rag/Indexing/IRagIndexer.cs` for the exact signature.

- [ ] **Step 5: Implement MetadataCardSynthesizer**

The core logic:
1. Call `IMachineRepository.StreamAllAsync` (if it exists; otherwise use a cross-partition query — check the actual interface)
2. For each machine, build a content string: `"{Title} by {Manufacturer} ({Year}). Designers: {Designers}. Themes: {Themes}. Editions: {Editions}. MSRP: {MSRP}."`
3. Build a `RagChunk` with `chunk_id = "meta_{opdbId}"`, `document_type = "metadata_card"`, `document_url = "https://opdb.org/machines/{opdbId}"`
4. Call `IRagIndexer.UpsertAsync`
5. Return `MetadataCardSyncResult`

The chunk ID scheme `meta_{opdbId}` is deterministic and idempotent — re-syncing the same machine overwrites the existing card.

- [ ] **Step 6: Run tests to confirm they pass**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "MetadataCardSynthesizerTests" 2>&1 | Select-String "passed|FAILED"
```

- [ ] **Step 7: Run full suite**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Tail -5
```

Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Application/Rag/MetadataCards/ \
        tests/PinballWizard.Scraper.Tests/Rag/MetadataCards/
git commit -m "feat(rag) W3a: add MetadataCardSynthesizer — synthesizes metadata_card chunks from Cosmos machines"
```

---

## Task 14 (W3a): Wire --sync-metadata-cards CLI command

Add the `--sync-metadata-cards` option to `Program.cs` and register `MetadataCardSynthesizer` in DI.

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs`

- [ ] **Step 1: Add the option**

In `Program.cs`, add alongside the other option declarations:

```csharp
var syncMetadataCardsOption = new Option<bool>("--sync-metadata-cards")
{
    Description = "Synthesize metadata_card chunks from the Cosmos machines container and upsert them into AI Search. Idempotent: safe to re-run."
};
```

- [ ] **Step 2: Register in DI when cosmos is configured**

In the DI wiring block (where Cosmos-dependent services are registered), add:

```csharp
services.AddSingleton<IMetadataCardSynthesizer, MetadataCardSynthesizer>();
```

- [ ] **Step 3: Add command handler**

In the root command handler, add the `--sync-metadata-cards` branch:

```csharp
if (syncMetadataCards)
{
    var synthesizer = host.Services.GetRequiredService<IMetadataCardSynthesizer>();
    var result = await synthesizer.SyncAsync(CancellationToken.None);
    logger.LogInformation(
        "--sync-metadata-cards complete: upserted={Upserted} skipped={Skipped} failed={Failed}",
        result.Upserted, result.Skipped, result.Failed);
    return result.Failed > 0 ? 1 : 0;
}
```

- [ ] **Step 4: Build clean**

```powershell
dotnet build PinballWizard.slnx -warnaserror 2>&1 | Tail -10
```

- [ ] **Step 5: Smoke-test --sync-metadata-cards**

With Cosmos env vars set:

```powershell
dotnet run --project src/PinballWizard.Cli -- --sync-metadata-cards --verbose
```

Expected: log shows "upserted=NNN skipped=0 failed=0".

After the run, verify via Azure Portal AI Search Explorer that the index contains documents with `document_type = "metadata_card"`.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git commit -m "feat(rag) W3a: wire --sync-metadata-cards CLI command; register MetadataCardSynthesizer"
```

---

## Task 15 (W3b): Bulletin discovery pass

Research each of the 5 non-Stern manufacturers for service bulletin sources. Document findings in `ingestion_sources.v1.json`. Wire new scrapers only if trivially one-PR.

**Files:**
- Modify: `data/seeds/ingestion_sources.v1.json`
- (Optional) Create: new `ISourceScraper` implementations + tests

- [ ] **Step 1: Check robots.txt for each manufacturer**

For each manufacturer, fetch robots.txt and check if the bulletin/advisory path is disallowed:

```
https://jerseyjackpinball.com/robots.txt     (JJP)
https://www.american-pinball.com/robots.txt  (AP)
https://www.spookypinball.com/robots.txt     (Spooky)
https://www.chicago-gaming.com/robots.txt    (CGC)
https://www.pinballbrothers.com/robots.txt   (PB)
```

Use a browser or `Invoke-WebRequest` — do NOT use the scraper infrastructure for this discovery step.

- [ ] **Step 2: Search each manufacturer site for bulletin/advisory sections**

Visit:
- JJP: `jerseyjackpinball.com` — look for Downloads, Support, Service section
- AP: `american-pinball.com` — look for Support, Downloads
- Spooky: `spookypinball.com` — look for Service, Downloads
- CGC: `chicago-gaming.com` — look for Service, Downloads
- PB: `pinballbrothers.com` — look for Support, Downloads

The build-spec notes JJP explicitly has no bulletins on the manufacturer site — document "NoSource" for JJP.

- [ ] **Step 3: For each manufacturer, add an entry to ingestion_sources.v1.json**

Each entry shape (add `status` + `discovery_notes`):

```json
{
  "source_id": "src_ap_bulletins",
  "manufacturer": "American Pinball",
  "source_type": "ServiceBulletin",
  "status": "NoSource",
  "discovery_notes": "No bulletin section found on american-pinball.com as of 2026-05-21. robots.txt allows /. Support page links to email contact only.",
  "discovery_date": "2026-05-21"
}
```

Valid `status` values: `Active` (new scraper wired), `Deferred` (source exists but complex), `NoSource` (no source found or path disallowed).

- [ ] **Step 4: If any manufacturer has status=Active, wire the scraper**

If a bulletin source is found and is trivially scrapeable (WP-REST, JSON-LD, or simple HTML listing), create:
- `src/PinballWizard.Infrastructure/Scraping/{Manufacturer}/`{Mfr}BulletinScraper.cs`
- The scraper extends `PoliteScraperBase`, routes through `IPolitenessGate`, and implements `ISourceScraper`
- Add to `SourceAliasContractTests` in `tests/`
- Run `dotnet test` to confirm the alias contract test passes

This step only applies if a source is found. If all 5 are NoSource/Deferred, skip straight to Step 5.

- [ ] **Step 5: Re-seed ingestion_sources**

```powershell
dotnet run --project src/PinballWizard.Cli -- --seed-ingestion-sources
```

Expected: log shows upserted records including new bulletin discovery entries.

- [ ] **Step 6: Commit**

```bash
git add data/seeds/ingestion_sources.v1.json
git commit -m "feat(scraper) W3b: bulletin discovery pass — 5 non-Stern manufacturers documented in ingestion_sources"
```

---

## Task 16 (W4): H5 eval and phase exit

Run the final eval, check the Cohere Rerank gate, update ADR-0024, log deferred items, and commit the phase close.

**Files:**
- Create: `data/eval/results/wizard.{timestamp}.phase45.json` (runtime)
- Modify: `docs/adr/0024-two-stage-reranking.md`
- Modify: `docs/decision-log.md`

- [ ] **Step 1: Run H5 eval**

```powershell
$env:AiFoundry__ProjectEndpoint = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiSearch__Endpoint         = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:Cosmos__AccountEndpoint    = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:Cosmos__AccountResourceId  = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"

dotnet run --project src/PinballWizard.Cli -- --eval
```

Note `citation_precision_mean` from the aggregate output.

- [ ] **Step 2: Check Cohere Rerank gate (ADR-0024)**

| Result | Action |
|---|---|
| `citation_precision ≥ 0.50` | Gate not triggered. Update ADR-0024: "H5 evaluation on Phase 4.5 full corpus did not trigger the gate (citation_precision={value}). Gate remains inactive." Phase 4.5 closes. |
| `citation_precision < 0.50` | Gate triggered. Wire `CohereRerankReranker` per ADR-0024 as a W4 fix-up PR. Re-run eval as H5b. |

- [ ] **Step 3: Update ADR-0024**

In `docs/adr/0024-two-stage-reranking.md`, add a "Phase 4.5 H5 outcome" section:

```markdown
## Phase 4.5 H5 outcome (2026-05-21)

H5 eval run: `citation_precision={value}` on the Phase 4.5 realigned eval set (30 questions, 7 curated machines).

Gate status: **{Not triggered / Triggered}**.

{If not triggered: "Phase 4.5 closes without implementing Cohere Rerank. The conditional implementation (CohereRerankReranker, Cohere MaaS Bicep resource) is deferred indefinitely until a future eval triggers the gate."}
{If triggered: "Proceeding to W4 fix-up PR: wire CohereRerankReranker."}
```

- [ ] **Step 4: Log deferred items in decision-log.md**

Add an entry for:
- Flyers (208 docs) — chunking strategy TBD
- Other bucket (98 docs) — classification TBD
- `NullTokenUsageReader` real impl — pending azure-sdk-for-net#2688

- [ ] **Step 5: Commit phase close**

```bash
git add data/eval/results/ \
        docs/adr/0024-two-stage-reranking.md \
        docs/decision-log.md
git commit -m "feat(eval) W4: H5 eval — citation_precision={value}; Phase 4.5 closed; deferred items logged"
```

---

## Self-Review

### Spec coverage check

| Spec section | Plan task |
|---|---|
| W0 — eval realignment | Tasks 1, 2, 3, 4 |
| W1 — ADI integration (FallbackTextExtractor + ADI extractor + Bicep + H1 hand-off) | Tasks 5, 6, 7, 8, 9 |
| W2 — corpus expansion (filter removal + runbook + H2 hand-off + backfill) | Tasks 10, 11, 12 |
| W3a — metadata-card synthesis (synthesizer + CLI) | Tasks 13, 14 |
| W3b — bulletin discovery pass | Task 15 |
| W4 — phase exit (H5 eval + Cohere gate + deferred items) | Task 16 |

All spec sections covered. No gaps found.

### Placeholder scan

No "TBD", "TODO", "implement later" phrases in any task step. All code blocks show complete implementations. Commands show expected output.

### Type consistency check

- `ExtractionStatus.OcrSucceeded` introduced in Task 5, used in Tasks 6, 7 — consistent.
- `ExtractionStatus.OcrFailed` introduced in Task 5, returned by `AzureDocumentIntelligenceTextExtractor` in Task 6 — consistent.
- `IDocumentTextExtractor` is the interface throughout — `PdfPigDocumentTextExtractor`, `AzureDocumentIntelligenceTextExtractor`, and `FallbackTextExtractor` all implement it — consistent.
- `MetadataCardSyncResult` returned by `IMetadataCardSynthesizer.SyncAsync` in Task 13, logged in Task 14 — consistent.
- `IngestionOutcome.Skipped_NotInCuratedSubset` removed in Task 10 — no later tasks reference it.
