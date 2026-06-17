# Game-Page Document Ingestion (Foundation + Stern Exemplar) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture Stern game-page documents (Rulesheet, Feature Matrix, Flyers) into the RAG corpus and surface them in rules answers, so "how do I reach wizard mode on Godzilla?" returns explicit objectives cited to the Stern rulesheet.

**Architecture:** Extend the shared document classifier with a `Rulesheet` type; add a document-section discovery pass to the Stern `GamePageScraper`; let the existing ingestion pipeline index the new types (with explicit large-PDF and thin-content handling); strengthen retrieval to pass the resolved `machine_id` for rules questions and boost rules document types via an AI Search scoring profile. Built manufacturer-agnostic so JJP/AP/etc. are follow-ons.

**Tech Stack:** .NET 10, Playwright (Stern Vue pages), Azure AI Search (data-plane SearchClient + scoring profiles), Azure Document Intelligence (PDF extraction), Cosmos Change Feed (ingestion), xUnit.

**Spec:** `docs/superpowers/specs/2026-06-16-game-page-documents-design.md`

**Branch:** `feat/game-page-documents`. Unit tests run without cloud deps; the Task 7 end-to-end eval needs the full local stack (devx + citation branches merged — see spec coupling note).

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/PinballWizard.Core/Models/Enums.cs` | `DocumentType` enum | Add `Rulesheet` |
| `src/PinballWizard.Application/ScraperOrchestrator.cs` | `ClassifyDocumentType` | Add `Rulesheet`; fix `FeatureMatrix`; make `internal` for test |
| index projection (locate in Task 1) | `DocumentType` → snake_case | Map `Rulesheet`→`rulesheet` |
| `src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs` | Stern discovery | Add document-section pass |
| ingestion filter/handler (locate in Task 4) | which docs get indexed; thin-content | Admit new types; degrade thin PDFs |
| `src/PinballWizard.Infrastructure/Rag/Indexing/AiSearchIndexSchema.cs` | index definition | Add a rules-boost scoring profile |
| `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs` | query building | Reference the scoring profile when rules-boost requested |
| `src/PinballWizard.Application/Ai/Tools/SearchCorpusTool.cs` + Wizard flow | tool args | Ensure `machineId` passed for rules; request boost |
| `src/PinballWizard.Application/Ai/Agents/Rules.md` | Rules prompt | Enumerate objectives when rulesheet present |
| `data/eval/...` (locate in Task 7) | eval set | Add Godzilla wizard-mode case |

---

## Task 1: Add the `Rulesheet` document type + index projection

**Files:**
- Modify: `src/PinballWizard.Core/Models/Enums.cs` (DocumentType enum, ~line 23-43)
- Locate + modify: the `DocumentType` → snake-case index projection (grep first — see Step 1)
- Test: the existing projection test (grep `metadata_card` in `tests/` to find it)

- [ ] **Step 1: Locate the projection.** Run: `grep -rn "metadata_card\|service_bulletin" src/ tests/ --include=*.cs | grep -iv "/bin/"`. The production mapping (a switch or dictionary `DocumentType` → string) is the projection; the test file asserting `MetadataCard => "metadata_card"` is where the new test goes. Read both.

- [ ] **Step 2: Write the failing test** in the projection test file, mirroring the existing `metadata_card` case:
```csharp
[Theory]
[InlineData(DocumentType.Rulesheet, "rulesheet")]
public void Projects_rulesheet_to_snake_case(DocumentType type, string expected)
    => Assert.Equal(expected, ProjectDocumentType(type)); // use the same call the existing cases use
```
(Match the existing test's invocation exactly — it may be a method, extension, or indexer.)

- [ ] **Step 3: Run it — expect FAIL** (compile error: `Rulesheet` undefined). Run the project's test command for that test class (e.g. `dotnet test --filter "FullyQualifiedName~<ProjectionTestClass>"`).

- [ ] **Step 4: Add the enum value.** In `Enums.cs`, add `Rulesheet,` to `DocumentType` (after `FeatureMatrix`, before `Readme`), with an XML-free comment matching the file's style noting the snake-case projection `rulesheet` (mirror the `MetadataCard` comment style).

- [ ] **Step 5: Add the projection case** `DocumentType.Rulesheet => "rulesheet"` in the mapping located in Step 1. Confirm `FeatureMatrix => "feature_matrix"` and `Flyer => "flyer"` cases exist; add them if the mapping omits them (they are unused today but Tasks 2-3 will emit them).

- [ ] **Step 6: Run the test — expect PASS.**

- [ ] **Step 7: Commit.**
```bash
git add src/PinballWizard.Core/Models/Enums.cs <projection-file> <projection-test-file>
git commit -m "feat(rag): add Rulesheet document type + snake-case projection"
```

---

## Task 2: Fix + extend the document classifier

**Files:**
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs` (`ClassifyDocumentType`, ~line 209-233)
- Test: Create `tests/PinballWizard.Application.Tests/Application/ScraperOrchestratorClassifyTests.cs`
- Possibly modify: `src/PinballWizard.Application/PinballWizard.Application.csproj` (InternalsVisibleTo)

- [ ] **Step 1: Make the method testable.** Change `private static DocumentType ClassifyDocumentType` to `internal static`. Confirm the Application project exposes internals to its test project: `grep -rn "InternalsVisibleTo" src/PinballWizard.Application`. If absent, add to the csproj:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="PinballWizard.Application.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing tests.** New test file:
```csharp
using PinballWizard.Application;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Application;

public sealed class ScraperOrchestratorClassifyTests
{
    private static DocumentType Classify(string linkText, string context = "Game Page → Documents")
        => ScraperOrchestrator.ClassifyDocumentType(
            new DiscoveredLink { FileUrl = "https://x/y.pdf", LinkText = linkText }, context);

    [Fact] public void Rulesheet_label_classifies_as_rulesheet()
        => Assert.Equal(DocumentType.Rulesheet, Classify("Godzilla Rulesheet Open PDF"));

    [Fact] public void Feature_matrix_label_classifies_as_feature_matrix_not_flyer()
        => Assert.Equal(DocumentType.FeatureMatrix, Classify("Godzilla Feature Matrix Open PDF"));

    [Fact] public void Flyer_label_classifies_as_flyer()
        => Assert.Equal(DocumentType.Flyer, Classify("Godzilla Pro Flyer Open PDF"));
}
```
(Confirm `DiscoveredLink`'s namespace from `grep -rn "class DiscoveredLink" src/` and add the using.)

- [ ] **Step 3: Run — expect FAIL** (`Feature Matrix` returns `Flyer`; `Rulesheet` returns `Other`). Run: `dotnet test --filter "FullyQualifiedName~ScraperOrchestratorClassifyTests"`.

- [ ] **Step 4: Fix the classifier.** In `ClassifyDocumentType`, in the text-based block, insert BEFORE the `text.Contains("flyer") || text.Contains("feature")` line:
```csharp
if (text.Contains("rulesheet") || url.Contains("rulesheet")) return DocumentType.Rulesheet;
if (text.Contains("feature matrix") || (text.Contains("feature") && text.Contains("matrix")))
    return DocumentType.FeatureMatrix;
```
and change `if (text.Contains("flyer") || text.Contains("feature"))` to `if (text.Contains("flyer"))`.

- [ ] **Step 5: Run — expect PASS** (all three).

- [ ] **Step 6: Commit.**
```bash
git add src/PinballWizard.Application/ScraperOrchestrator.cs tests/PinballWizard.Application.Tests/Application/ScraperOrchestratorClassifyTests.cs src/PinballWizard.Application/PinballWizard.Application.csproj
git commit -m "fix(scraper): classify Rulesheet + fix Feature Matrix misclassified as Flyer"
```

---

## Task 3: Capture the Stern game-page document section

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageScraperDocumentSectionTests.cs`

- [ ] **Step 1: Read the current scraper.** Read `GamePageScraper.cs` fully — note `ScrapeAsync` (the tab loop ~line 93-115), `ScrapeTabAsync` (~line 205-278), the `EvaluateAsync` link selector (`a[href*="wp-content/uploads"]` etc.), and how raw links become `DiscoveredLink` (the projection ~line 255-262). The new pass mirrors this extraction but is NOT tab-gated.

- [ ] **Step 2: Write the failing test.** The extraction is pure given a set of raw `(href, text)` pairs → `DiscoveredLink`s. Refactor the link-projection into an `internal static` method `BuildDocumentSectionLinks(IEnumerable<LinkRaw> raw, DiscoveredGame game)` and test it with the verified Godzilla labels:
```csharp
[Fact]
public void Builds_discovered_links_for_each_game_page_document_with_documents_context()
{
    var raw = new[]
    {
        new LinkRaw { href = "https://sternpinball.com/wp-content/uploads/2022/06/Godzilla-Rulesheet.pdf", text = "Godzilla Rulesheet Open PDF" },
        new LinkRaw { href = "https://sternpinball.com/wp-content/uploads/2021/09/Godzilla-Pinball-Feature-Matrix-x.pdf", text = "Godzilla Feature Matrix Open PDF" },
        new LinkRaw { href = "https://sternpinball.com/wp-content/uploads/2023/11/GODZILLA-PRO.pdf", text = "Godzilla Pro Flyer Open PDF" },
    };
    var links = GamePageScraper.BuildDocumentSectionLinks(raw, GodzillaGame());
    Assert.Equal(3, links.Count);
    Assert.All(links, l => Assert.Equal("Game Page → Documents", l.DiscoveryContext));
    Assert.Contains(links, l => l.FileUrl.EndsWith("Godzilla-Rulesheet.pdf"));
}
```
(Use the real `LinkRaw`/`DiscoveredGame` shapes from the file; add a small `GodzillaGame()` builder.)

- [ ] **Step 3: Run — expect FAIL** (method does not exist). Run: `dotnet test --filter "FullyQualifiedName~GamePageScraperDocumentSectionTests"`.

- [ ] **Step 4: Implement.** Add `internal static List<DiscoveredLink> BuildDocumentSectionLinks(...)` projecting each raw link to a `DiscoveredLink` with `DiscoveryContext = "Game Page → Documents"`, `GameSlug = game.Slug`, `Tab = null`. Then in `ScrapeAsync`, after the tab loop, add a document-section pass: run the same page `EvaluateAsync` link query (no tab click; the docs render by default — confirmed via Playwright that the Godzilla rulesheet link is `visible:true` without a tab click), pass results through `BuildDocumentSectionLinks`, and append to the returned links. De-dupe by `FileUrl` against tab-discovered links.

- [ ] **Step 5: Run — expect PASS.**

- [ ] **Step 6: Commit.**
```bash
git add src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageScraperDocumentSectionTests.cs
git commit -m "feat(scraper): capture the Stern game-page document section (rulesheet, feature matrix, flyers)"
```

---

## Task 4: Ingestion — admit new types + large-PDF + thin-content handling

**Files:**
- Investigate + modify: download size cap (locate), ingestion type filter (locate)
- Modify: the extraction/chunking step for thin-content degradation
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/...` (mirror existing extractor/ingestion tests)

- [ ] **Step 1: Confirm the indexing gap (spec open item #1).** Read `src/PinballWizard.Infrastructure/Rag/Ingestion/ScrapedDocumentChangeFeedHandler.cs` (~line 100-110, document_type parse) and search for any type whitelist: `grep -rniE "DocumentType\.(Manual|ServiceBulletin)\b|document_type ==|IsIndexable|Skip" src/PinballWizard.Infrastructure/Rag --include=*.cs`. Determine whether non-Manual/Bulletin types are dropped before indexing. Document the finding in the commit message.

- [ ] **Step 2: Confirm the download size cap (spec open item #2).** Read `src/PinballWizard.Infrastructure/Downloading/FileDownloader.cs`; find any max-bytes guard. The Godzilla rulesheet is ~19.7 MB — ensure the cap (if any) admits it (raise to a logged, bounded ceiling, e.g. 64 MB, if lower).

- [ ] **Step 3: Write the failing thin-content test.** For the extractor/chunker step that turns extracted text into chunks, assert that text below a threshold yields zero chunks AND records a low-text signal (not an exception). Mirror the existing extractor test fixture:
```csharp
[Fact]
public async Task Below_min_text_threshold_yields_no_chunks_and_logs_low_text()
{
    // Arrange: a document whose extracted text length < MinChunkableTextChars
    // Act: run the chunk/ingest step
    // Assert: chunks is empty; the low-text metric/log was emitted; no throw.
}
```
(Use the project's existing meter-assertion pattern — `ConcurrentBag` + `Assert.Contains` with a tag predicate, per the established MeterListener test pattern.)

- [ ] **Step 4: Run — expect FAIL.** `dotnet test --filter "FullyQualifiedName~<thin-content-test>"`.

- [ ] **Step 5: Implement.** (a) If Step 1 found a whitelist, add `Rulesheet`, `FeatureMatrix`, `Flyer`. (b) In the chunking step, when extracted text length is below a `MinChunkableTextChars` threshold (add to the relevant options, default conservative e.g. 200), skip chunk emission, log + increment a `low_text` metric, and continue (no throw). Provenance (the document record) is still written.

- [ ] **Step 6: Run — expect PASS.**

- [ ] **Step 7: Commit.**
```bash
git add -A src/PinballWizard.Infrastructure/Rag src/PinballWizard.Infrastructure/Downloading tests/PinballWizard.Infrastructure.Tests/Rag
git commit -m "feat(rag): ingest game-page doc types; handle large PDFs and thin/image-heavy content"
```

---

## Task 5: Retrieval — machine-scope for rules + doc-type boost scoring profile

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Rag/Indexing/AiSearchIndexSchema.cs`
- Modify: `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs` (`BuildSearchOptionsCore`)
- Modify: `src/PinballWizard.Application/Ai/Tools/SearchCorpusTool.cs` + the Wizard/Rules invocation path
- Modify: `src/PinballWizard.Application/Ai/Agents/Rules.md`
- Test: retriever options test + tool test

- [ ] **Step 1: Read the retrieval path.** Read `AiSearchRagRetriever.BuildSearchOptionsCore`/`BuildFilter` (machine_id + document_type filtering already exist via `RetrievalOptions`), `RetrievalOptions` (fields), `AiSearchIndexSchema` (does it define scoring profiles?), and how the Wizard/AiRouter invokes `SearchCorpusTool` (can it inject the resolved `machineId`, or is it purely model-driven?). Capture the machine-injection mechanism before Step 4.

- [ ] **Step 2: Add a scoring profile (failing test first).** Test that the index definition includes a scoring profile (e.g. `rules-boost`) that boosts `document_type` values `rulesheet`/`feature_matrix`:
```csharp
[Fact]
public void Index_defines_rules_boost_scoring_profile()
{
    var index = AiSearchIndexSchema.Build(/* existing args */);
    Assert.Contains(index.ScoringProfiles, p => p.Name == "rules-boost");
}
```
Run → FAIL. Implement: add a `ScoringProfile("rules-boost")` with a tag/term boost favouring `document_type` rules values (Azure.Search.Documents `ScoringProfile` + `TagScoringFunction`/text-weight). Run → PASS.

- [ ] **Step 3: Wire the boost into retrieval (failing test first).** Add a `bool BoostRules` (or `string? ScoringProfile`) to `RetrievalOptions`; test that `BuildSearchOptionsCore` sets `SearchOptions.ScoringProfile = "rules-boost"` when requested and leaves it null otherwise:
```csharp
[Fact]
public void Sets_scoring_profile_when_rules_boost_requested()
{
    var o = BuildSearchOptionsCore(vec, OptionsWith(boostRules: true), semanticCfg);
    Assert.Equal("rules-boost", o.ScoringProfile);
}
```
Run → FAIL → implement → PASS.

- [ ] **Step 4: Pass machine_id + boost for rules questions.** Per Step 1's finding, make the Rules path deterministically supply the resolved `machineId` and request the rules boost to `searchCorpus` (either the Wizard injects the arg, or `SearchCorpusTool` reads the resolved machine from the call context and sets `RetrievalOptions.MachineId` + `BoostRules`). Add a test asserting that when a machine is resolved, the retrieval is invoked with the machine filter set. Run → FAIL → implement → PASS.

- [ ] **Step 5: Rules prompt tweak.** In `Agents/Rules.md` Step 2, add: when the provided corpus content includes a rulesheet, enumerate the specific objectives/requirements it lists (do not summarise away the steps) and cite the rulesheet document URL. (No code test; covered by the Task 7 eval.)

- [ ] **Step 6: Commit.**
```bash
git add src/PinballWizard.Infrastructure/Rag src/PinballWizard.Application/Ai tests/
git commit -m "feat(rag): machine-scope rules retrieval + rules-boost scoring profile + Rules prompt"
```

---

## Task 6: Backfill runbook

**Files:**
- Create: `docs/runbooks/stern-game-page-doc-backfill.md`

- [ ] **Step 1: Write the runbook** documenting: re-run the Stern game-page scrape (the CLI `--source` for the Stern game-page scraper — confirm the alias via `dotnet run --project src/PinballWizard.Cli -- --help`), Godzilla-first validation, then all Stern games; the 2s politeness delay; expected embedding-cost growth; and how to verify in the index (`document_type` facet now shows `rulesheet`/`feature_matrix`/`flyer`; the rulesheet `document_url` is searchable).

- [ ] **Step 2: Commit.**
```bash
git add docs/runbooks/stern-game-page-doc-backfill.md
git commit -m "docs(runbook): Stern game-page document backfill"
```

---

## Task 7: End-to-end eval entry (success criterion)

**Files:**
- Locate + modify: the eval ground-truth set (`grep -rln "wizard mode\|godzilla" data/eval --include=*.json`)
- Test: the eval harness run (integration; needs the full local stack)

- [ ] **Step 1: Add the eval case.** Add a ground-truth entry: question "How do I reach wizard mode on Godzilla? What objectives must I complete?"; expectation = a grounded answer whose citations include the Stern Godzilla rulesheet `document_url`. Match the existing eval-entry schema (read a sibling entry first).

- [ ] **Step 2: Document the run.** This case passes only after the backfill (Task 6) populates the rulesheet and the full local stack is up (devx + citation merged). Note in the eval entry / runbook that it is gated on backfill.

- [ ] **Step 3: Commit.**
```bash
git add data/eval/...
git commit -m "test(eval): Godzilla wizard-mode objectives cited to the Stern rulesheet"
```

---

## Notes for the implementer

- **Branch coupling:** unit tests (Tasks 1-5) run standalone. The Task 7 eval needs the local Foundry stack, which currently requires the `fix/aspire-launch-type` + `fix/citation-sink-captive-dependency` branches merged to `main`. Coordinate before running the eval.
- **Manufacturer-agnostic:** the classifier (Task 2), ingestion (Task 4), and retrieval (Task 5) changes are shared. Only Task 3 (discovery) is Stern-specific. JJP/AP/etc. reuse Tasks 1/2/4/5 and add their own discovery — see spec §9.
- **Politeness:** unchanged — transparent UA, robots respected, 2s delay; `wp-content/uploads` is robots-allowed for the project's UA.
