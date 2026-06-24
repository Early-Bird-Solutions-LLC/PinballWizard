# Stern Public Game-Page Enrichment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Index public `sternpinball.com/game/{slug}/` descriptive content (per-edition deltas + long-form overview), the Feature Matrix PDF, the trailer link, and merchandise — so the Wizard can answer per-game feature/edition questions with citations.

**Architecture:** The scraper extracts new content from the already-rendered game-page DOM into `GameRecord`; the reconciler persists it onto the OPDB-keyed `Machine`; edition deltas ride the existing MetadataCard path while long-form prose becomes a new chunked `GameOverview` document indexed via a new `--sync-game-overviews` Cli verb (mirroring `--sync-metadata-cards`). The Feature Matrix PDF joins the RAG accept-list, and a new counter makes type-filtered drops observable.

**Tech Stack:** C# / .NET 10, AngleSharp (DOM parse of rendered HTML), Playwright (page render, existing), Azure AI Search (`IRagIndexer`), `Microsoft.ML.Tokenizers` (cl100k_base), xUnit, `System.Diagnostics.Metrics`.

## Global Constraints

- **Target .NET 10**; `Nullable` enabled; `.editorconfig` is the style source.
- **Personal identity only** — every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer**.
- **Polite-by-construction** — no new outbound HTTP; reuse the single already-rendered page load in `GamePageScraper`. No bare `HttpClient.GetAsync`.
- **Provenance is sacred** — every indexed artifact carries the real game-page URL; never drop `Source`/`DiscoveryUrl`/`GameSlug`.
- **Fallbacks must not hide failures** — absent trailer/accessories/prose degrade to empty/null **visibly**; never fabricate.
- **Tests assert behavior** — fixtures must exercise the real extraction/edition logic, not just shape.
- **No XML doc comments** required on public surface (repo convention).
- `document_type` is written to the index as `DocumentType.ToString()` (NO snake_case at write time); the snake_case alias is read-side only in `SearchCorpusTool.NormalizeDocumentType`.
- Work entirely in the worktree `.worktrees/stern-gamepage-enrichment` on branch `feat/stern-gamepage-enrichment`.
- Build/test command (CI-equivalent filter): `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`. Per-test runs use `--filter "FullyQualifiedName~<TestName>"`.

---

### Task 1: Add `DocumentType.GameOverview` + read-side alias

**Files:**
- Modify: `src/PinballWizard.Core/Models/Enums.cs` (DocumentType enum, after `MetadataCard`)
- Modify: `src/PinballWizard.Application/Ai/Tools/SearchCorpusTool.cs` (`NormalizeDocumentType`, ~line 388-402)
- Test: `tests/PinballWizard.Infrastructure.Tests/Ai/Tools/SearchCorpusToolTests.cs` (existing file; add a case)

**Interfaces:**
- Produces: `DocumentType.GameOverview` enum member; `NormalizeDocumentType("game_overview") == "GameOverview"`.

- [ ] **Step 1: Write the failing test** — add to `SearchCorpusToolTests`:

```csharp
[Theory]
[InlineData("game_overview", "GameOverview")]
[InlineData("GameOverview", "GameOverview")]
public void NormalizeDocumentType_GameOverview_MapsToEnumString(string input, string expected)
{
    Assert.Equal(expected, SearchCorpusTool.NormalizeDocumentType(input));
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~NormalizeDocumentType_GameOverview"`
Expected: FAIL — returns `"game_overview"` (passthrough) instead of `"GameOverview"`.

- [ ] **Step 3: Implement** — in `Enums.cs`, add after the `MetadataCard` member (keep the existing trailing comma style):

```csharp
    /// <summary>
    /// Synthesized long-form game-overview card built from a Machine's
    /// OverviewProse + per-edition sections by GameOverviewSynthesizer.
    /// Per the index contract, projects via .ToString() to "GameOverview";
    /// the read-side snake_case alias is "game_overview".
    /// </summary>
    GameOverview,
```

In `SearchCorpusTool.NormalizeDocumentType`, add a switch arm alongside the existing ones:

```csharp
        "game_overview" => "GameOverview",
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~NormalizeDocumentType_GameOverview"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Models/Enums.cs src/PinballWizard.Application/Ai/Tools/SearchCorpusTool.cs tests/PinballWizard.Infrastructure.Tests/Ai/Tools/SearchCorpusToolTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(rag) add DocumentType.GameOverview + read-side alias"
```

---

### Task 2: Add new content fields to `GameRecord` and `Machine`

**Files:**
- Modify: `src/PinballWizard.Core/Models/GameRecord.cs`
- Modify: `src/PinballWizard.Core/Domain/Machine.cs`
- Test: `tests/PinballWizard.Core.Tests/Models/GameRecordFieldsTests.cs` (create)

**Interfaces:**
- Produces:
  - `GameRecord.OverviewProse : string?`, `GameRecord.TrailerUrl : string?`, `GameRecord.Accessories : List<AccessoryInfo>`, `GameRecord.ShopCollectionUrl : string?`
  - `AccessoryInfo { string Name; string? Price; string ProductUrl; string? ImageUrl }`
  - `Machine.OverviewProse : string?`, `Machine.OverviewSourceUrl : string?`, `Machine.TrailerUrl : string?`, `Machine.Accessories : List<MachineAccessory>`
  - `MachineAccessory { string Name; string? Price; string ProductUrl; string? ImageUrl }`

- [ ] **Step 1: Write the failing test** — create `GameRecordFieldsTests.cs`:

```csharp
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Core.Tests.Models;

public sealed class GameRecordFieldsTests
{
    [Fact]
    public void GameRecord_NewContentFields_DefaultEmpty()
    {
        var g = new GameRecord
        {
            GameId = "game_pokemon", Title = "Pokémon", Slug = "pokemon",
            GamePageUrl = "https://sternpinball.com/game/pokemon/"
        };
        Assert.Null(g.OverviewProse);
        Assert.Null(g.TrailerUrl);
        Assert.Null(g.ShopCollectionUrl);
        Assert.Empty(g.Accessories);
    }

    [Fact]
    public void Machine_NewContentFields_DefaultEmpty()
    {
        var m = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla"
        };
        Assert.Null(m.OverviewProse);
        Assert.Null(m.OverviewSourceUrl);
        Assert.Null(m.TrailerUrl);
        Assert.Empty(m.Accessories);
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GameRecordFieldsTests"`
Expected: FAIL — does not compile (members missing).

- [ ] **Step 3: Implement** — in `GameRecord.cs`, add to `GameRecord` (after `Editions`):

```csharp
    /// <summary>Game-level descriptive prose scraped from the game page (not edition-specific).</summary>
    public string? OverviewProse { get; set; }

    /// <summary>YouTube trailer watch URL from the game page embed, if present.</summary>
    public string? TrailerUrl { get; set; }

    /// <summary>Per-game accessories from the "Stern Shop" section of the game page.</summary>
    public List<AccessoryInfo> Accessories { get; set; } = [];

    /// <summary>"View All" shop collection URL for this game's accessories.</summary>
    public string? ShopCollectionUrl { get; set; }
```

Add the `AccessoryInfo` type to `GameRecord.cs` (after `EditionInfo`):

```csharp
public sealed class AccessoryInfo
{
    public required string Name { get; set; }
    public string? Price { get; set; }
    public required string ProductUrl { get; set; }
    public string? ImageUrl { get; set; }
}
```

In `Machine.cs`, add to `Machine` (after `Editions`):

```csharp
    /// <summary>Scraper-owned: game-level overview prose from the manufacturer game page.</summary>
    [JsonPropertyName("overviewProse")]
    public string? OverviewProse { get; set; }

    /// <summary>Scraper-owned: canonical game-page URL the overview prose was scraped from (provenance for the GameOverview doc).</summary>
    [JsonPropertyName("overviewSourceUrl")]
    public string? OverviewSourceUrl { get; set; }

    /// <summary>Scraper-owned: YouTube trailer watch URL from the manufacturer game page.</summary>
    [JsonPropertyName("trailerUrl")]
    public string? TrailerUrl { get; set; }

    /// <summary>Scraper-owned: per-game accessories from the manufacturer shop section.</summary>
    [JsonPropertyName("accessories")]
    public List<MachineAccessory> Accessories { get; set; } = [];
```

Add `MachineAccessory` to `Machine.cs` (after `MachineEdition`):

```csharp
public sealed class MachineAccessory
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("price")] public string? Price { get; set; }
    [JsonPropertyName("productUrl")] public required string ProductUrl { get; set; }
    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }
}
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GameRecordFieldsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Models/GameRecord.cs src/PinballWizard.Core/Domain/Machine.cs tests/PinballWizard.Core.Tests/Models/GameRecordFieldsTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(catalog) GameRecord+Machine overview/trailer/accessory fields"
```

---

### Task 3: Reconciler persists the new fields onto `Machine`

**Files:**
- Modify: `src/PinballWizard.Application/Sync/ScraperReconciliationService.cs` (`ApplyScraperFields`, ~line 203-215)
- Test: `tests/PinballWizard.Application.Tests/Sync/ScraperReconciliationServiceTests.cs` (existing file — add a fact; if no such file exists, create it following the test in `tests/` that already covers `NormalizeFranchiseTitle`/`ReconcileAsync`)

**Interfaces:**
- Consumes: `GameRecord.OverviewProse/TrailerUrl/Accessories/GamePageUrl` (Task 2), `Machine.OverviewProse/OverviewSourceUrl/TrailerUrl/Accessories` (Task 2).
- Note: edition `Description`/`UniqueFeatures` already flow via existing `MapEdition` (lines 217-225) — **do not** re-add them.

- [ ] **Step 1: Write the failing test** — add to the reconciliation test class. Use the existing test's harness for building a partition + repository fake; assert the copy:

```csharp
[Fact]
public async Task ReconcileAsync_CopiesOverviewTrailerAndAccessories_OntoMatchedMachine()
{
    var machine = new Machine
    {
        Id = "GweeP-MW95j", PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla",
        ManufacturerSlugs = { ["stern"] = "godzilla" }
    };
    var repo = new FakeMachineRepository([machine]);          // existing test double
    var svc = new ScraperReconciliationService(repo, TimeProvider.System, NullLogger<ScraperReconciliationService>.Instance);

    var catalog = new GameCatalog();
    catalog.Games.Add(new GameRecord
    {
        GameId = "game_godzilla", Title = "Godzilla", Slug = "godzilla",
        GamePageUrl = "https://sternpinball.com/game/godzilla/",
        OverviewProse = "Battle Godzilla across the city.",
        TrailerUrl = "https://www.youtube.com/watch?v=abc123",
        Accessories = { new AccessoryInfo { Name = "Topper", Price = "$1,299.99", ProductUrl = "https://shop.sternpinball.com/products/godzilla-topper" } }
    });

    await svc.ReconcileAsync(catalog, CancellationToken.None);

    var saved = repo.Saved.Single();
    Assert.Equal("Battle Godzilla across the city.", saved.OverviewProse);
    Assert.Equal("https://sternpinball.com/game/godzilla/", saved.OverviewSourceUrl);
    Assert.Equal("https://www.youtube.com/watch?v=abc123", saved.TrailerUrl);
    Assert.Equal("Topper", saved.Accessories.Single().Name);
}
```

(If the existing test file uses a differently-named repository fake, reuse that one — read the file first and match its harness; do not introduce a second fake.)

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~CopiesOverviewTrailerAndAccessories"`
Expected: FAIL — `saved.OverviewProse` is null.

- [ ] **Step 3: Implement** — in `ApplyScraperFields`, after the `machine.Editions = ...` line and before `machine.LastSeenAt = now;`, add:

```csharp
        // Overview prose + its provenance URL, trailer, and accessories are
        // scraper-owned game-page content (the manufacturer page is fresher
        // and richer than OPDB for these). Replace wholesale.
        machine.OverviewProse = game.OverviewProse;
        machine.OverviewSourceUrl = string.IsNullOrWhiteSpace(game.OverviewProse) ? null : game.GamePageUrl;
        machine.TrailerUrl = game.TrailerUrl;
        machine.Accessories = game.Accessories
            .Select(a => new MachineAccessory { Name = a.Name, Price = a.Price, ProductUrl = a.ProductUrl, ImageUrl = a.ImageUrl })
            .ToList();
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~CopiesOverviewTrailerAndAccessories"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Sync/ScraperReconciliationService.cs tests/PinballWizard.Application.Tests/Sync/ScraperReconciliationServiceTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(sync) reconcile overview/trailer/accessories onto Machine"
```

---

### Task 4: `FeatureMatrix` classification branch

**Files:**
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs` (`ClassifyDocumentType`, ~line 295-318 — change `private static` to `internal static`)
- Test: `tests/PinballWizard.Application.Tests/ScraperOrchestratorClassifyTests.cs` (create)

**Pre-step (read, do not guess):** Confirm `[assembly: InternalsVisibleTo("PinballWizard.Application.Tests")]` exists for the Application assembly (it must, since `SearchCorpusTool.NormalizeDocumentType` is `internal` and tested). Grep: `grep -rn InternalsVisibleTo src/PinballWizard.Application`. If the test project that sees Application internals is `PinballWizard.Infrastructure.Tests` instead, put this test there and use that namespace.

**Interfaces:**
- Produces: `ScraperOrchestrator.ClassifyDocumentType(DiscoveredLink, string)` becomes `internal static`; returns `DocumentType.FeatureMatrix` for feature-matrix links.

- [ ] **Step 1: Write the failing test**:

```csharp
using PinballWizard.Application;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using Xunit;

namespace PinballWizard.Application.Tests;

public sealed class ScraperOrchestratorClassifyTests
{
    private static DiscoveredLink Link(string url, string? text) =>
        new() { FileUrl = url, LinkText = text, DiscoveryContext = "Game Page → Promotional Materials tab", GameSlug = "pokemon" };

    [Theory]
    [InlineData("https://sternpinball.com/wp-content/uploads/2026/02/PANTS-Matrix.pdf", "Pokémon by Stern Pinball Feature Matrix")]
    [InlineData("https://sternpinball.com/x/matrix.pdf", "Game Feature Matrix")]
    public void ClassifyDocumentType_FeatureMatrix_Detected(string url, string text)
    {
        Assert.Equal(DocumentType.FeatureMatrix, ScraperOrchestrator.ClassifyDocumentType(Link(url, text), "Game Page → Promotional Materials tab"));
    }

    [Fact]
    public void ClassifyDocumentType_PlainFlyer_StillFlyer()
    {
        Assert.Equal(DocumentType.Flyer, ScraperOrchestrator.ClassifyDocumentType(Link("https://x/PANTS-PRO-Flyer.pdf", "Pokémon Pro Flyer"), "Game Page → Promotional Materials tab"));
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~ClassifyDocumentType_FeatureMatrix"`
Expected: FAIL — currently classifies "feature matrix" as `Flyer` (the `text.Contains("feature")` arm).

- [ ] **Step 3: Implement** — change the signature to `internal static DocumentType ClassifyDocumentType(...)`. Then add a feature-matrix arm **before** the existing `if (text.Contains("flyer") || text.Contains("feature"))` line so it wins:

```csharp
        if (text.Contains("feature matrix") || text.Contains("matrix")) return DocumentType.FeatureMatrix;
        if (url.Contains("matrix")) return DocumentType.FeatureMatrix;
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~ClassifyDocumentType"`
Expected: PASS (both the FeatureMatrix theory and the plain-Flyer fact).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/ScraperOrchestrator.cs tests/PinballWizard.Application.Tests/ScraperOrchestratorClassifyTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(scraper) classify Feature Matrix PDFs as FeatureMatrix"
```

---

### Task 5: Accept `FeatureMatrix` into RAG ingestion

**Files:**
- Modify: `src/PinballWizard.Core/Configuration/RagIngestionOptions.cs` (line 21-22 default list)
- Test: `tests/PinballWizard.Core.Tests/Configuration/RagIngestionOptionsTests.cs` (create)

**Interfaces:**
- Produces: `new RagIngestionOptions().AcceptedDocumentTypes` contains `Manual`, `ServiceBulletin`, `FeatureMatrix`.

- [ ] **Step 1: Write the failing test**:

```csharp
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Core.Tests.Configuration;

public sealed class RagIngestionOptionsTests
{
    [Fact]
    public void Default_AcceptedTypes_IncludeFeatureMatrix()
    {
        var accepted = new RagIngestionOptions().AcceptedDocumentTypes;
        Assert.Contains(DocumentType.Manual, accepted);
        Assert.Contains(DocumentType.ServiceBulletin, accepted);
        Assert.Contains(DocumentType.FeatureMatrix, accepted);
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~Default_AcceptedTypes_IncludeFeatureMatrix"`
Expected: FAIL — `FeatureMatrix` not in the list.

- [ ] **Step 3: Implement** — update the default and its comment:

```csharp
    // Document types accepted by the pipeline. Manuals + service bulletins +
    // feature matrices (the per-edition feature table — gameplay-relevant).
    // The metadata-card / game-overview synthesis paths flow through Cli sync
    // verbs, NOT this list. Anything outside returns Skipped_DocumentTypeFiltered.
    [Required]
    [MinLength(1)]
    public List<DocumentType> AcceptedDocumentTypes { get; set; } =
        [DocumentType.Manual, DocumentType.ServiceBulletin, DocumentType.FeatureMatrix];
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~Default_AcceptedTypes_IncludeFeatureMatrix"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Configuration/RagIngestionOptions.cs tests/PinballWizard.Core.Tests/Configuration/RagIngestionOptionsTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(rag) accept FeatureMatrix documents into ingestion"
```

---

### Task 6: Make type-filtered drops observable (counter)

**Files:**
- Modify: `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs` (add counter near the other `pinwiz.rag.*` counters)
- Modify: `src/PinballWizard.Infrastructure/Rag/Ingestion/ScrapedDocumentChangeFeedHandler.cs` (~line 88-94)
- Modify: `src/PinballWizard.Application/Rag/Ingestion/ScrapedDocumentIngestionPipeline.cs` (~line 80-86)
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/Ingestion/RagChangefeedTelemetryTests.cs` (add to existing file, reuse its MeterListener helper pattern)

**Interfaces:**
- Produces: `PinballWizardTelemetry.RagIngestionTypeFiltered : Counter<long>` (`pinwiz.rag.ingestion_type_filtered_total`), incremented with a `document_type` tag at both filter sites.

- [ ] **Step 1: Write the failing test** — add to `RagChangefeedTelemetryTests`, mirroring its existing `ConcurrentBag` + `MeterListener` helper (collect `long` measurements, filter by the `document_type` tag):

```csharp
[Fact]
public async Task ChangeFeedHandler_FiltersUnacceptedType_EmitsTypeFilteredCounter()
{
    var samples = new ConcurrentBag<(long Value, string? Type)>();
    using var l = new MeterListener();
    l.SetMeasurementEventCallback<long>((_, value, tags, _) =>
    {
        string? type = null;
        foreach (var t in tags) if (t.Key == "document_type") type = t.Value as string;
        samples.Add((value, type));
    });
    l.Start();
    l.EnableMeasurementEvents(PinballWizardTelemetry.RagIngestionTypeFiltered);

    var ctx = NewHandlerContext();                          // existing harness in this file
    // a Flyer is NOT in the accepted set → must be filtered + metered
    await ctx.Service.HandleChangesAsync([NewChange("doc_1", DocumentType.Flyer)], CancellationToken.None);

    Assert.Contains(samples, s => s.Type == "Flyer" && s.Value >= 1);
}
```

(Read the file first: reuse its existing `NewHandlerContext`/`NewChange` helpers and accepted-types wiring; do not invent new harness if equivalents exist.)

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~EmitsTypeFilteredCounter"`
Expected: FAIL — `RagIngestionTypeFiltered` does not exist (compile error), then no measurement once it compiles.

- [ ] **Step 3: Implement** — in `PinballWizardTelemetry.cs`, add near `RagChangefeedDeadLetterTotal`:

```csharp
    public static readonly Counter<long> RagIngestionTypeFiltered = Meter.CreateCounter<long>(
        "pinwiz.rag.ingestion_type_filtered_total",
        unit: "{document}",
        description: "Documents skipped before download because their document_type is not in the RAG accepted-types set. Tagged with document_type. A persistent nonzero rate for a type you EXPECT to ingest means a classification or accept-list gap — the silent-drop class that hid the Domain-2 gameplay gap.");
```

In `ScrapedDocumentChangeFeedHandler.cs`, inside the `if (!_acceptedTypes.Contains(documentType))` block, keep the existing `LogDebug` and add before `return`:

```csharp
        PinballWizardTelemetry.RagIngestionTypeFiltered.Add(
            1, new KeyValuePair<string, object?>("document_type", documentType.ToString()));
```

In `ScrapedDocumentIngestionPipeline.cs`, inside its `if (!_acceptedTypes.Contains(change.DocumentType))` block, keep the existing `LogDebug` and add before `return`:

```csharp
        PinballWizardTelemetry.RagIngestionTypeFiltered.Add(
            1, new KeyValuePair<string, object?>("document_type", change.DocumentType.ToString()));
```

(`PinballWizardTelemetry` is in `PinballWizard.Application.Observability`; add a `using` if the pipeline file lacks it. The change-feed handler is in Infrastructure but already references Application telemetry elsewhere — confirm the `using` resolves.)

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~EmitsTypeFilteredCounter"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs src/PinballWizard.Infrastructure/Rag/Ingestion/ScrapedDocumentChangeFeedHandler.cs src/PinballWizard.Application/Rag/Ingestion/ScrapedDocumentIngestionPipeline.cs tests/PinballWizard.Infrastructure.Tests/Rag/Ingestion/RagChangefeedTelemetryTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(obs) meter type-filtered RAG ingestion drops (invariant #17)"
```

---

### Task 7: `GameOverviewSynthesizer` (edition-preserving, multi-chunk)

**Files:**
- Create: `src/PinballWizard.Application/Rag/GameOverviews/IGameOverviewSynthesizer.cs`
- Create: `src/PinballWizard.Application/Rag/GameOverviews/GameOverviewSynthesizer.cs`
- Create: `src/PinballWizard.Application/Rag/GameOverviews/ServiceCollectionExtensions.cs` (DI: `AddGameOverviewSynthesizer`, mirror `Rag/MetadataCards/ServiceCollectionExtensions.cs`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/GameOverviews/GameOverviewSynthesizerTests.cs`

**Interfaces:**
- Consumes: `Machine` (with `OverviewProse` + `Editions[].Description/UniqueFeatures` from Tasks 2/3), `Chunk` record (`Rag/Chunking/Chunk.cs`).
- Produces: `IGameOverviewSynthesizer.Synthesize(Machine) : IReadOnlyList<Chunk>` — one chunk for the overview, one per edition that has `Description` or `UniqueFeatures`. `SectionHeading` is `"Overview"` or `"Edition: {name}"`. Returns empty list when `OverviewProse` is blank AND no edition has content (no fabrication).

Design note: this synthesizer owns its own semantic chunking (one chunk per section) — it does NOT depend on `HybridChunker`. Token counts use `TiktokenTokenizer.CreateForEncoding("cl100k_base")` exactly as `MetadataCardSynthesizer` does.

- [ ] **Step 1: Write the failing test** (mirror `MetadataCardSynthesizerTests` style — inline `Machine` fixtures, `NullLogger`):

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Rag.GameOverviews;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.GameOverviews;

public sealed class GameOverviewSynthesizerTests
{
    private static GameOverviewSynthesizer New() => new(NullLogger<GameOverviewSynthesizer>.Instance);

    private static Machine Godzilla() => new()
    {
        Id = "GweeP-MW95j", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
        Title = "Godzilla",
        OverviewProse = "Battle Godzilla and rival kaiju across the city in this SPIKE-2 machine.",
        Editions =
        [
            new MachineEdition { Name = "Pro", Description = "Core layout." },
            new MachineEdition { Name = "Limited Edition", Description = "Adds a magna-grab and mechanical building.", UniqueFeatures = ["magna-grab", "mechanical building"] },
        ],
    };

    [Fact]
    public void Synthesize_PreservesSharedProseAndEditionDeltas()
    {
        var chunks = New().Synthesize(Godzilla());

        // shared overview present
        Assert.Contains(chunks, c => c.SectionHeading == "Overview" && c.Text.Contains("Battle Godzilla", StringComparison.Ordinal));
        // LE-specific content preserved + attributed to its edition
        Assert.Contains(chunks, c => c.SectionHeading == "Edition: Limited Edition"
            && c.Text.Contains("magna-grab", StringComparison.Ordinal));
        // Pro is its own chunk, not merged with LE
        Assert.Contains(chunks, c => c.SectionHeading == "Edition: Pro");
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Synthesize_NoContent_ReturnsEmpty_NoFabrication()
    {
        var bare = new Machine { Id = "X-1", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball", Title = "Mystery" };
        Assert.Empty(New().Synthesize(bare));
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GameOverviewSynthesizerTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement** — `IGameOverviewSynthesizer.cs`:

```csharp
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Rag.GameOverviews;

public interface IGameOverviewSynthesizer
{
    IReadOnlyList<Chunk> Synthesize(Machine machine);
}
```

`GameOverviewSynthesizer.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Rag.GameOverviews;

// Builds a chunked GameOverview document from a Machine's scraped game-page
// content. One chunk per semantic section: the shared overview prose, then one
// per edition carrying that edition's Description + UniqueFeatures. Per-edition
// chunking keeps edition-specific answers ("what's different about the LE?")
// retrievable as distinct units. No HybridChunker dependency — the sections ARE
// the chunk boundaries. Returns empty when there is nothing to say.
public sealed class GameOverviewSynthesizer : IGameOverviewSynthesizer
{
    private readonly TiktokenTokenizer _tokenizer;
    private readonly ILogger<GameOverviewSynthesizer> _logger;

    public GameOverviewSynthesizer(ILogger<GameOverviewSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    public IReadOnlyList<Chunk> Synthesize(Machine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var chunks = new List<Chunk>();
        var index = 0;

        if (!string.IsNullOrWhiteSpace(machine.OverviewProse))
        {
            var text = $"{machine.Title} — Overview\n{machine.OverviewProse.Trim()}";
            chunks.Add(new Chunk(index++, text, "Overview", 0, 0, _tokenizer.CountTokens(text)));
        }

        foreach (var edition in machine.Editions)
        {
            var body = BuildEditionBody(edition);
            if (body is null) continue;
            var text = $"{machine.Title} — {edition.Name}\n{body}";
            chunks.Add(new Chunk(index++, text, $"Edition: {edition.Name}", 0, 0, _tokenizer.CountTokens(text)));
        }

        _logger.LogDebug(
            "GameOverview synthesized: machineId={MachineId} title={Title} chunks={ChunkCount}.",
            machine.Id, machine.Title, chunks.Count);

        return chunks;
    }

    private static string? BuildEditionBody(MachineEdition edition)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(edition.Description)) sb.Append(edition.Description.Trim());
        if (edition.UniqueFeatures.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("Unique features: ").Append(string.Join(", ", edition.UniqueFeatures)).Append('.');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
```

`ServiceCollectionExtensions.cs` — mirror the MetadataCards one exactly (read it first, copy the registration shape):

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace PinballWizard.Application.Rag.GameOverviews;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGameOverviewSynthesizer(this IServiceCollection services)
    {
        services.AddSingleton<IGameOverviewSynthesizer, GameOverviewSynthesizer>();
        return services;
    }
}
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GameOverviewSynthesizerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Rag/GameOverviews/ tests/PinballWizard.Infrastructure.Tests/Rag/GameOverviews/
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(rag) GameOverviewSynthesizer (edition-preserving, per-section chunks)"
```

---

### Task 8: `--sync-game-overviews` Cli verb

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs` (add option + handler block + DI registration, mirroring `--sync-metadata-cards` at lines ~198, ~351-431, ~864)
- Test: covered by the live-run verification in Task 11 (the verb is thin glue over Task 7's synthesizer + the existing `IRagIndexer`; its units are already tested). Add a Cli option-parse smoke test only if `tests/PinballWizard.Cli.Tests` has an existing option-parsing test to mirror.

**Pre-step (read, do not guess):** Read `src/PinballWizard.Cli/Program.cs` lines 190-210 (option declaration) and 345-431 (`--sync-metadata-cards` handler) to copy the exact `Option<bool>` declaration style, the DI-resolution block, the `ChunkRequest` construction, and the `indexer.UpsertAsync(...)` call. Reuse `RagIndexerOptions` defaults as that block does.

**Interfaces:**
- Consumes: `IGameOverviewSynthesizer` (Task 7), `IMachineRepository.StreamByManufacturerAsync`, `IRagIndexer.UpsertAsync`, `ChunkRequest` (`MachineId, MachineTitle, Manufacturer, DocumentId, DocumentUrl, DocumentType, LastScrapedUtc?, Edition?, EditionScope?`).

- [ ] **Step 1: Register DI** — in the `if (cosmosWired && aiSearchWired && foundryWired)` block where `AddMetadataCardSynthesizer()` is called (~line 864), add:

```csharp
            builder.Services.AddGameOverviewSynthesizer();
```

- [ ] **Step 2: Declare the option** — beside `syncMetadataCardsOption`:

```csharp
        var syncGameOverviewsOption = new Option<bool>("--sync-game-overviews")
        {
            Description = "Synthesize and index GameOverview documents from each Machine's scraped game-page OverviewProse + per-edition content. Mirrors --sync-metadata-cards. No-op for machines without overview content.",
        };
```

Add it to the root command's options collection the same way `syncMetadataCardsOption` is added.

- [ ] **Step 3: Implement the handler** — mirror the `--sync-metadata-cards` block; resolve `IGameOverviewSynthesizer` instead of `IMetadataCardSynthesizer`, skip machines whose synthesis yields zero chunks, and build the request from the machine's overview provenance URL:

```csharp
        if (parseResult.GetValue(syncGameOverviewsOption))
        {
            var machineRepo = host.Services.GetRequiredService<IMachineRepository>();
            var synthesizer = host.Services.GetRequiredService<IGameOverviewSynthesizer>();
            var indexer = host.Services.GetRequiredService<IRagIndexer>();
            var indexerOptions = new RagIndexerOptions();
            var indexed = 0; var skipped = 0;

            foreach (var manufacturer in allManufacturers)
            {
                await foreach (var machine in machineRepo.StreamByManufacturerAsync(manufacturer, cancellationToken))
                {
                    var chunks = synthesizer.Synthesize(machine);
                    if (chunks.Count == 0 || string.IsNullOrWhiteSpace(machine.OverviewSourceUrl))
                    {
                        skipped++;
                        continue;
                    }

                    var request = new ChunkRequest(
                        MachineId: machine.Id,
                        MachineTitle: machine.Title,
                        Manufacturer: machine.ManufacturerDisplayName,
                        DocumentId: $"overview_{machine.Id}",
                        DocumentUrl: machine.OverviewSourceUrl,
                        DocumentType: DocumentType.GameOverview,
                        LastScrapedUtc: machine.LastSeenAt == default ? null : machine.LastSeenAt);

                    await indexer.UpsertAsync(request, chunks, indexerOptions, cancellationToken);
                    indexed++;
                }
            }

            Console.WriteLine($"GameOverview sync complete: indexed={indexed} skipped(no-content)={skipped}.");
            return 0;
        }
```

(Match the surrounding block's exact variable names — `allManufacturers`, `host`, `cancellationToken`, `RagIndexerOptions` — as read in the pre-step. Adjust if the metadata-cards block differs.)

- [ ] **Step 4: Verify it builds**

Run: `dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(cli) --sync-game-overviews indexes GameOverview docs from Machines"
```

---

### Task 9: `GamePageContentExtractor` — prose, trailer, accessories, per-edition (pure, testable)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Scraping/Stern/GamePageContentExtractor.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageContentExtractorTests.cs`

Design: a `static` class taking an AngleSharp `IDocument` (parsed from the rendered HTML the scraper already has) + the game `Uri`, returning the new content. This mirrors `JjpProductExtractor` (pure static, inline-HTML fixtures) so it is testable WITHOUT Playwright. The selectors below are grounded in the observed Pokémon page structure (descriptive `<p>` blocks in the edition content area; a YouTube `<iframe>`/anchor; the "STERN SHOP" product links to `shop.sternpinball.com/products/...`; the Promotional-Materials/Specs tabs of document links).

**Interfaces:**
- Produces:
  - `GamePageContentExtractor.ExtractOverviewProse(IDocument) : string?`
  - `GamePageContentExtractor.ExtractTrailerUrl(IDocument) : string?` (normalized `https://www.youtube.com/watch?v=<id>`)
  - `GamePageContentExtractor.ExtractAccessories(IDocument) : List<AccessoryInfo>`
  - `GamePageContentExtractor.ExtractShopCollectionUrl(IDocument) : string?`

- [ ] **Step 1: Write the failing test** — inline HTML fixtures (trimmed shapes of the real page):

```csharp
using AngleSharp.Html.Parser;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

public sealed class GamePageContentExtractorTests
{
    private static AngleSharp.Dom.IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    [Fact]
    public void ExtractTrailerUrl_FromYouTubeIframe_Normalized()
    {
        var doc = Parse("""<div><iframe src="https://www.youtube.com/embed/78q_9-6PBSY?rel=0"></iframe></div>""");
        Assert.Equal("https://www.youtube.com/watch?v=78q_9-6PBSY", GamePageContentExtractor.ExtractTrailerUrl(doc));
    }

    [Fact]
    public void ExtractTrailerUrl_None_ReturnsNull()
    {
        Assert.Null(GamePageContentExtractor.ExtractTrailerUrl(Parse("<div>no video</div>")));
    }

    [Fact]
    public void ExtractAccessories_FromShopSection_NameAndPriceAndUrl()
    {
        var html = """
        <section><h2>Stern Shop</h2>
          <a href="https://shop.sternpinball.com/collections/pokemon-accessories-and-parts">View All</a>
          <a href="https://shop.sternpinball.com/products/pokemon-by-stern-pinball-topper">
            <img src="https://cdn/topper.jpg"/>
            <span>Pokémon by Stern Pinball Topper</span><span>$1,499.99</span>
          </a>
        </section>
        """;
        var doc = Parse(html);
        var items = GamePageContentExtractor.ExtractAccessories(doc);
        var topper = Assert.Single(items);
        Assert.Equal("Pokémon by Stern Pinball Topper", topper.Name);
        Assert.Equal("$1,499.99", topper.Price);
        Assert.Equal("https://shop.sternpinball.com/products/pokemon-by-stern-pinball-topper", topper.ProductUrl);
        Assert.Equal("https://shop.sternpinball.com/collections/pokemon-accessories-and-parts",
            GamePageContentExtractor.ExtractShopCollectionUrl(doc));
    }

    [Fact]
    public void ExtractOverviewProse_JoinsDescriptiveParagraphs()
    {
        var html = """
        <div class="game-content">
          <p>Players shoot the Poké Ball to catch Pokémon.</p>
          <p>Premium and Limited Edition games include an interactive electromagnet.</p>
        </div>
        """;
        var prose = GamePageContentExtractor.ExtractOverviewProse(Parse(html));
        Assert.Contains("catch Pokémon", prose, StringComparison.Ordinal);
        Assert.Contains("electromagnet", prose, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GamePageContentExtractorTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement** — `GamePageContentExtractor.cs`. Use AngleSharp DOM queries; normalize YouTube embed/watch/youtu.be forms to a canonical watch URL; filter accessories to `shop.sternpinball.com/products/` anchors. Keep it defensive: every method returns null/empty rather than throwing when the shape is absent (no fabrication).

```csharp
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Stern;

// Pure extractor over the rendered Stern game-page DOM. Mirrors JjpProductExtractor:
// static, no I/O, fed the already-rendered HTML the scraper holds. Every method
// degrades to null/empty when its shape is absent — never fabricates.
public static partial class GamePageContentExtractor
{
    [GeneratedRegex(@"(?:youtube\.com/(?:embed/|watch\?v=)|youtu\.be/)([A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeId();

    public static string? ExtractTrailerUrl(IDocument doc)
    {
        foreach (var el in doc.QuerySelectorAll("iframe[src], a[href]"))
        {
            var url = el.GetAttribute("src") ?? el.GetAttribute("href");
            if (string.IsNullOrEmpty(url)) continue;
            var m = YouTubeId().Match(url);
            if (m.Success) return $"https://www.youtube.com/watch?v={m.Groups[1].Value}";
        }
        return null;
    }

    public static string? ExtractShopCollectionUrl(IDocument doc)
    {
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (href is not null && href.Contains("shop.sternpinball.com/collections/", StringComparison.OrdinalIgnoreCase))
                return href;
        }
        return null;
    }

    public static List<AccessoryInfo> ExtractAccessories(IDocument doc)
    {
        var items = new List<AccessoryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (href is null || !href.Contains("shop.sternpinball.com/products/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(href)) continue;

            var spans = a.QuerySelectorAll("span");
            string? name = null, price = null;
            foreach (var s in spans)
            {
                var t = s.TextContent?.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith('$')) price ??= t;
                else name ??= t;
            }
            if (string.IsNullOrEmpty(name)) continue;
            var img = a.QuerySelector("img")?.GetAttribute("src");
            items.Add(new AccessoryInfo { Name = name, Price = price, ProductUrl = href, ImageUrl = img });
        }
        return items;
    }

    public static string? ExtractOverviewProse(IDocument doc)
    {
        // Descriptive paragraphs live in the game content/edition area. Join the
        // non-trivial <p> blocks; the answer model tolerates incidental marketing.
        var sb = new StringBuilder();
        foreach (var p in doc.QuerySelectorAll("p"))
        {
            var t = p.TextContent?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length < 40) continue;   // skip nav/labels/short fragments
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(t);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
```

Note: `<40` is a heuristic to drop nav/label fragments, not a content filter — descriptive paragraphs on the real page are 200-600 chars. If Task 11's live run shows real prose being dropped, lower the threshold; do not raise it to silently exclude content.

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GamePageContentExtractorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/Stern/GamePageContentExtractor.cs tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageContentExtractorTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(scraper) GamePageContentExtractor: prose, trailer, accessories"
```

---

### Task 10: Wire the extractor into `GamePageScraper` (populate `GameRecord`)

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs` (`ExtractGameMetadataAsync`, ~line 151-206 — it already parses the rendered HTML into an AngleSharp `doc`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageScraperContentTests.cs` (create — if `GamePageScraper.ExtractGameMetadataAsync` is private, test through the extractor + a small `internal` mapping helper; see step note)

**Interfaces:**
- Consumes: `GamePageContentExtractor` (Task 9), the existing `doc` in `ExtractGameMetadataAsync`.
- Produces: the returned `GameRecord` now has `OverviewProse`, `TrailerUrl`, `Accessories`, `ShopCollectionUrl` populated from the page.

Note on testability: `ExtractGameMetadataAsync` takes a live Playwright `IPage`, so it is not unit-testable directly. Extract a pure `internal static GameRecord ApplyPageContent(GameRecord record, IDocument doc)` helper on `GamePageScraper` (or a sibling mapper) that the scraper calls after building the base `GameRecord`. Unit-test that helper with an inline-HTML `IDocument`. This keeps the new logic behind a tested boundary without driving Playwright in tests (consistent with how the existing scraper isolates `GamePageExtractors`).

- [ ] **Step 1: Write the failing test**:

```csharp
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

public sealed class GamePageScraperContentTests
{
    [Fact]
    public void ApplyPageContent_PopulatesOverviewTrailerAccessories()
    {
        var html = """
        <html><body>
          <iframe src="https://www.youtube.com/embed/78q_9-6PBSY"></iframe>
          <p>Players shoot the illuminated Poké Ball to catch Pokémon and battle Team Rocket.</p>
          <a href="https://shop.sternpinball.com/collections/pokemon-accessories-and-parts">View All</a>
          <a href="https://shop.sternpinball.com/products/pokemon-topper"><span>Pokémon Topper</span><span>$1,499.99</span></a>
        </body></html>
        """;
        var doc = new HtmlParser().ParseDocument(html);
        var record = new GameRecord { GameId = "game_pokemon", Title = "Pokémon", Slug = "pokemon", GamePageUrl = "https://sternpinball.com/game/pokemon/" };

        var enriched = GamePageScraper.ApplyPageContent(record, doc);

        Assert.Contains("catch Pokémon", enriched.OverviewProse!, StringComparison.Ordinal);
        Assert.Equal("https://www.youtube.com/watch?v=78q_9-6PBSY", enriched.TrailerUrl);
        Assert.Equal("Pokémon Topper", enriched.Accessories.Single().Name);
        Assert.Equal("https://shop.sternpinball.com/collections/pokemon-accessories-and-parts", enriched.ShopCollectionUrl);
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GamePageScraperContentTests"`
Expected: FAIL — `ApplyPageContent` does not exist.

- [ ] **Step 3: Implement** — add to `GamePageScraper`:

```csharp
    // Pure mapping of rendered-page content onto the GameRecord. internal for
    // unit testing without driving Playwright (see GamePageScraperContentTests).
    internal static GameRecord ApplyPageContent(GameRecord record, AngleSharp.Dom.IDocument doc)
    {
        record.OverviewProse = GamePageContentExtractor.ExtractOverviewProse(doc);
        record.TrailerUrl = GamePageContentExtractor.ExtractTrailerUrl(doc);
        record.Accessories = GamePageContentExtractor.ExtractAccessories(doc);
        record.ShopCollectionUrl = GamePageContentExtractor.ExtractShopCollectionUrl(doc);
        return record;
    }
```

In `ExtractGameMetadataAsync`, after the `return new GameRecord { ... }` is built, route it through the helper. Concretely, assign the constructed record to a local and apply before returning:

```csharp
            var record = new GameRecord
            {
                // ... existing initializer ...
            };
            return ApplyPageContent(record, doc);
```

(The method already has `doc` in scope from `parser.ParseDocument(html)`.) Ensure `GamePageScraper`'s assembly has `[InternalsVisibleTo("PinballWizard.Infrastructure.Tests")]` — it does (the existing `LinkRaw` is `internal` "so SternPlaywrightDtoActivatorContractTests ... can assert"). 

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~GamePageScraperContentTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageScraperContentTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(scraper) GamePageScraper populates overview/trailer/accessories"
```

---

### Task 11: Per-edition prose extraction + full-suite verification + live spot-check

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs` (populate `EditionInfo.Description`/`UniqueFeatures` from the per-edition tab content — A1)
- Modify/Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageScraperContentTests.cs` (add an edition-content case)

**Pre-step (read + live spot-check, do not guess):** The open design question from the spec — *is all Pro/Premium/LE prose present in the rendered DOM at once, or does it require a tab click per edition?* Resolve it before coding: run the existing AppHost or a one-off Playwright render of `sternpinball.com/game/pokemon/` and inspect whether edition-specific `Description` text for all three editions is in a single `page.ContentAsync()` snapshot or only after clicking each edition control. If a click-per-edition is required, extend the edition walk (the scraper already clicks tabs in `ScrapeTabAsync`); if all editions are in the DOM, parse them directly. **Do not** invent the selector — derive it from the live DOM, then encode it.

- [ ] **Step 1: Write the failing test** — encode the resolved per-edition shape (example assuming editions render as labeled blocks; adjust selector to the live finding):

```csharp
[Fact]
public void ApplyEditionContent_PreservesPerEditionDescriptions()
{
    // Shape mirrors the resolved live DOM for edition content blocks.
    var html = """
    <div data-edition="Pro"><p>Pro core layout description here for the game.</p></div>
    <div data-edition="Limited Edition"><p>LE adds an interactive electromagnet and mirrored backglass.</p></div>
    """;
    var doc = new HtmlParser().ParseDocument(html);
    var record = new GameRecord
    {
        GameId = "game_pokemon", Title = "Pokémon", Slug = "pokemon",
        GamePageUrl = "https://sternpinball.com/game/pokemon/",
        Editions = { new EditionInfo { Name = "Pro" }, new EditionInfo { Name = "Limited Edition" } }
    };

    GamePageScraper.ApplyEditionContent(record, doc);

    Assert.Contains("core layout", record.Editions.Single(e => e.Name == "Pro").Description!, StringComparison.Ordinal);
    Assert.Contains("electromagnet", record.Editions.Single(e => e.Name == "Limited Edition").Description!, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~ApplyEditionContent"`
Expected: FAIL — `ApplyEditionContent` does not exist.

- [ ] **Step 3: Implement** — add an `internal static void ApplyEditionContent(GameRecord, IDocument)` that matches each existing `EditionInfo.Name` to its edition block in the DOM (using the selector resolved in the pre-step) and sets `Description` (and `UniqueFeatures` if the page exposes a feature list per edition). Call it from `ApplyPageContent` (Task 10) so the single page render feeds both. Preserve every edition's distinct text — never overwrite one edition's `Description` with another's.

- [ ] **Step 4: Run the per-test, then the full CI-equivalent suite**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~ApplyEditionContent"` → Expected: PASS.
Then the full gate (per memory: filtered subsets miss cross-file contract tests):
Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: PASS (0 failures). Investigate any `SourceAliasContractTests` / `CosmosOptionsTests` / doc-conformance failures — those are the cross-file pins.

- [ ] **Step 5: Live end-to-end spot-check (manual, documented in the PR)**

Against the live index (no AI Search emulator exists — this uses the live service via `DefaultAzureCredential`):
1. Run a scrape so the enriched `Machine` records persist (overview/trailer/accessories/edition deltas).
2. `dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers` if needed.
3. `dotnet run --project src/PinballWizard.Cli -- --sync-game-overviews` → confirm `indexed > 0`.
4. Ask the Wizard a known-answerable edition question (e.g. "What's different about the Pokémon LE?") and confirm a grounded answer with a `game_overview` citation pointing at the game page URL.
5. Record the before/after in the PR description (this is the behavior proof the showcase bar requires).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs tests/PinballWizard.Infrastructure.Tests/Scraping/Stern/GamePageScraperContentTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(scraper) per-edition description capture (A1) + verify suite"
```

---

## Pre-PR gate (after all tasks)

- [ ] Run `/local-review` (qualitative) and `/standards-audit` (mechanical) in the worktree — treat 🔴 as blocking (CLAUDE.md PR self-audit).
- [ ] Sibling-diff check: this only touches Stern; confirm no JsonLd/OpenGraph sibling drift introduced.
- [ ] Open the PR with `gh pr create`, add + verify the `claude-code` label, put the full PR URL in the response, record both audit outcomes + the Task 11 live spot-check in the description.

## Self-review notes (author)

- **Spec coverage:** A1 (edition deltas) → Tasks 2,3,9,11; A2 (GameOverview doc) → Tasks 1,2,3,7,8; B (Feature Matrix) → Tasks 4,5; C (trailer) → Tasks 2,3,9,10; D (merch) → Tasks 2,3,9,10; adjacent counter → Task 6. All spec sections map to tasks.
- **Open items deferred to live spot-check (Task 11 pre-step):** the edition-tab DOM shape (single render vs click-per-edition) and the `ExtractOverviewProse` length threshold — both explicitly flagged as "resolve from live DOM, do not guess," per the no-guessing rule.
- **Type consistency:** `ChunkRequest`/`Chunk` field names match `Rag/Chunking/Chunk.cs`; `IRagIndexer.UpsertAsync` signature matches `IRagIndexer.cs`; `document_type` written via `.ToString()` (Task 1 alias is read-side only).
