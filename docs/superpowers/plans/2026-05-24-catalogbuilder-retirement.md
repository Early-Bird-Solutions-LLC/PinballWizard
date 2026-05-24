# CatalogBuilder Retirement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the file-based fallback path from `ScraperOrchestrator`, delete `CatalogBuilder.cs` and `MigrateToRawCommand.cs`, make `IRawDocumentRepository` a required constructor parameter, update DI registration in `Program.cs`, archive `data/metadata/catalog.json` + `games.json`, and update affected tests.

**Architecture:** `ScraperOrchestrator` currently has two code paths: a Cosmos path (when `_rawDocRepo is not null`) and a file-based fallback (when null). Since every environment now provides Cosmos (Aspire emulator for local dev, deployed account for production), the null path is dead code. Removing it eliminates `CatalogBuilder` as a runtime dependency — it is used only in the null path. `DownloadAsync` and `BuildCatalogAsync` also call `_catalogBuilder`; those methods become vestigial once Cosmos is required and must be assessed. The `--migrate-to-raw` one-time backfill has already run and its command is safe to delete.

**Tech Stack:** C# 14, .NET 10, xUnit, NSubstitute, `Microsoft.Extensions.DependencyInjection`

**Pre-condition (BLOCKING):** Before merging this branch to main, verify the production Cosmos `scraped_documents_raw` document count matches the expected post-backfill count. Run `dotnet run --project src/PinballWizard.Cli -- --status` against production config (or query Cosmos directly) and record the count in the PR description. Do not merge until this is confirmed.

---

## File Map

| File | Change |
|---|---|
| `src/PinballWizard.Application/ScraperOrchestrator.cs` | Remove `CatalogBuilder` field + param; make `IRawDocumentRepository` required; remove null-path branches in `ScrapeAsync`; assess `DownloadAsync` + `BuildCatalogAsync` |
| `src/PinballWizard.Application/Provenance/CatalogBuilder.cs` | Delete |
| `src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs` | Delete |
| `src/PinballWizard.Cli/Program.cs` | Remove `CatalogBuilder` transient; replace factory registration with `AddSingleton`; remove `--migrate-to-raw` option + handler |
| `data/metadata/catalog.json` | Move to `data/archive/catalog.json` |
| `data/metadata/games.json` | Move to `data/archive/games.json` |
| `tests/PinballWizard.Scraper.Tests/ScraperOrchestratorTests.cs` | Update `CreateOrchestrator` helper; remove or update catalog-path tests |
| `tests/PinballWizard.Scraper.Tests/IntegrationTests.cs` | Remove `CatalogBuilder` DI assertion; remove catalog-path integration test |
| `tests/PinballWizard.Scraper.Tests/CatalogBuilderTests.cs` | Delete |

---

### Task 1: Create feature branch

- [ ] **Step 1: Create branch**

```bash
git checkout main && git pull
git checkout -b feature/catalogbuilder-retirement
```

- [ ] **Step 2: Confirm starting commit**

```bash
git log --oneline -1
```

---

### Task 2: Assess `DownloadAsync` and `BuildCatalogAsync`

Before writing code, understand what stays and what goes. Both methods call `_catalogBuilder` today.

**Files:**
- Read: `src/PinballWizard.Application/ScraperOrchestrator.cs` lines 239–363

- [ ] **Step 1: Evaluate DownloadAsync**

`DownloadAsync` (lines 239–313 of `ScraperOrchestrator.cs`) calls:
- `_catalogBuilder.LoadCatalogAsync` — loads `data/metadata/catalog.json`
- `_catalogBuilder.ApplyDownloadResult` — stamps file info on a `DocumentRecord`
- `_catalogBuilder.SaveCatalogAsync` — writes `data/metadata/catalog.json`

In a Cosmos-first world, `DownloadAsync` is file-catalog–only functionality. The linked document download use case is handled by the linker job reading `scraped_documents` and downloading via the existing `IFileDownloader`. **Decision: delete `DownloadAsync` from `ScraperOrchestrator`.** Any CLI flag that invokes it (`--download`, `--download-all`) will also be removed.

- [ ] **Step 2: Evaluate BuildCatalogAsync**

`BuildCatalogAsync` (lines 320–363) calls:
- `_catalogBuilder.LoadCatalogAsync`
- `_catalogBuilder.LoadGameCatalogAsync`
- `_catalogBuilder.LinkDocumentsToGames`
- `_catalogBuilder.ResolveCoverPageLinksAsync`
- `_catalogBuilder.SaveCatalogAsync`

This is pure file-catalog reconciliation. With Cosmos as the source of truth this method has no valid Cosmos-path caller. **Decision: delete `BuildCatalogAsync` from `ScraperOrchestrator`.** The `--build-catalog` CLI flag will also be removed.

- [ ] **Step 3: Evaluate PrintStatusAsync**

`PrintStatusAsync` (lines 368–418) calls `_catalogBuilder.LoadCatalogAsync` and `LoadGameCatalogAsync`. It prints a summary of the file-based catalog. **Decision: delete `PrintStatusAsync`.** The `--status` CLI flag will be removed (Cosmos-side status is observable via the Admin UI or direct Cosmos queries).

- [ ] **Step 4: Evaluate game record handling in ScrapeAsync**

At `ScraperOrchestrator.cs:63–66`, `_catalogBuilder.MergeGameRecord(gameCatalog, item.Game)` is called even on the Cosmos path. OPDB sync (`OpdbSyncService`) is the authoritative game catalog writer — it writes to `IMachineRepository`. Scraped `item.Game` references are populated from scraper metadata (slug, title, game page URL) and are already included in the `GameReference` embedded in `RawDocumentRecord` via `BuildGameReference`. Once `CatalogBuilder` is deleted, the game catalog merge just goes away — no replacement needed because `IMachineRepository` (OPDB) is authoritative and scraped game metadata is captured inside `RawDocumentRecord.Game`.

---

### Task 3: Rewrite `ScraperOrchestrator.cs`

**Files:**
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs`

- [ ] **Step 1: Remove dead fields, constructor param, and using directives**

Delete:
- `private readonly CatalogBuilder _catalogBuilder;` (line 19)
- `private readonly IRawDocumentRepository? _rawDocRepo;` (line 22, make it non-nullable)
- The `CatalogBuilder catalogBuilder` constructor parameter (line 28)
- `_catalogBuilder = catalogBuilder;` assignment (line 35)
- `_rawDocRepo = rawDocRepo;` assignment (line 37) — rename param `rawDocRepo` to non-optional
- The `using PinballWizard.Application.Provenance;` import at the top

New constructor signature:

```csharp
public ScraperOrchestrator(
    IEnumerable<ISourceScraper> scrapers,
    IFileDownloader downloader,
    IRawDocumentRepository rawDocRepo,
    IOptions<ScraperSettings> settings,
    ILogger<ScraperOrchestrator> logger)
{
    _scrapers = scrapers;
    _downloader = downloader;
    _rawDocRepo = rawDocRepo;
    _settings = settings.Value;
    _logger = logger;
}
```

Change `IRawDocumentRepository?` field to `IRawDocumentRepository` (non-nullable).

- [ ] **Step 2: Simplify ScrapeAsync — remove the null-path branch**

In `ScrapeAsync`, the current body loads `catalog` and `gameCatalog` from `_catalogBuilder` at the top. Remove those two lines entirely. Then, inside the `foreach`, the `if (_rawDocRepo is not null) { ... } else { ... }` block becomes just the Cosmos path:

```csharp
if (item.Link is not null)
{
    var record = BuildDocumentRecord(item);
    try
    {
        await _rawDocRepo.UpsertRawAsync(record, cancellationToken);
        result.TotalLinks++;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.LogError(ex, "Failed to upsert {DocumentId} to scraped_documents_raw", record.DocumentId);
        result.Errors.Add($"{record.DocumentId}: {ex.Message}");
    }
}
```

Also remove:
- The `if (item.Game is not null)` block calling `_catalogBuilder.MergeGameRecord` (lines 63–66) — just remove the whole block; `result.GamesDiscovered` can be removed from `ScrapeResult` too.
- The `if (_rawDocRepo is null) { ... }` block at lines 115–128 (catalog link passes + SaveCatalogAsync).
- The `await _catalogBuilder.SaveGameCatalogAsync(gameCatalog, cancellationToken);` call at line 132.

- [ ] **Step 3: Delete DownloadAsync, BuildCatalogAsync, PrintStatusAsync**

Delete the three methods entirely (lines 239–418). Leave the class structure clean with `ScrapeAsync`, `BuildDocumentRecord`, the private `ClassifyActionType`, `ClassifyDocumentType`, `BuildGameReference`, `FilterScrapers` methods, and the `SourceAliases` dictionary.

- [ ] **Step 4: Update `ScrapeResult` class**

Remove `GamesDiscovered` from `ScrapeResult` if you removed the game-tracking block. Update the log message in `ScrapeAsync` that references it:

```csharp
_logger.LogInformation(
    "Scrape complete: {Total} links, {Errors} errors",
    result.TotalLinks, result.Errors.Count);
```

- [ ] **Step 5: Build the Application project to verify no compile errors**

```bash
dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 4: Delete `CatalogBuilder.cs`

**Files:**
- Delete: `src/PinballWizard.Application/Provenance/CatalogBuilder.cs`

- [ ] **Step 1: Delete the file**

```bash
rm src/PinballWizard.Application/Provenance/CatalogBuilder.cs
```

- [ ] **Step 2: Check for any remaining references to CatalogBuilder in Application project**

```bash
grep -rn "CatalogBuilder" src/PinballWizard.Application/
```

Expected: no output. Fix any references found.

- [ ] **Step 3: Build Application project**

```bash
dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 5: Update `Program.cs` in CLI project

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs`

- [ ] **Step 1: Remove `--migrate-to-raw` option declaration**

Delete the `migrateToRawOption` variable declaration (lines 141–144):

```csharp
// DELETE THIS:
var migrateToRawOption = new Option<bool>("--migrate-to-raw")
{
    Description = "..."
};
```

- [ ] **Step 2: Remove `--migrate-to-raw` from rootCommand.Options**

Delete `rootCommand.Options.Add(migrateToRawOption);` (line 165).

- [ ] **Step 3: Remove `--download`, `--download-all`, `--build-catalog`, `--status` options**

These options call `DownloadAsync`, `BuildCatalogAsync`, and `PrintStatusAsync` which no longer exist. Delete:
- `downloadOption`, `downloadAllOption`, `buildCatalogOption` (if present), `statusOption` variable declarations
- Their `rootCommand.Options.Add(...)` lines
- Their `parseResult.GetValue(...)` assignments in `SetAction`
- The `if (status)`, `if (download || downloadAll)` handler blocks in `SetAction`

Note: `--scrape-only` and `--dry-run` remain valid since they gate `ScrapeAsync`.

- [ ] **Step 4: Remove `migrateToRaw` variable and handler in SetAction**

Delete `var migrateToRaw = parseResult.GetValue(migrateToRawOption);` and the `if (migrateToRaw) { await MigrateToRawCommand.RunAsync(...); return; }` block (lines 405–411).

- [ ] **Step 5: Remove `CatalogBuilder` transient registration**

In `CreateHost`, delete:

```csharp
// DELETE THIS:
builder.Services.AddTransient<CatalogBuilder>();
```

- [ ] **Step 6: Replace the ScraperOrchestrator factory registration with direct singleton**

Current (lines 859–866):

```csharp
builder.Services.AddTransient<ScraperOrchestrator>(sp => new ScraperOrchestrator(
    sp.GetRequiredService<IEnumerable<ISourceScraper>>(),
    sp.GetRequiredService<IFileDownloader>(),
    sp.GetRequiredService<CatalogBuilder>(),
    sp.GetRequiredService<IOptions<ScraperSettings>>(),
    sp.GetRequiredService<ILogger<ScraperOrchestrator>>(),
    sp.GetService<IRawDocumentRepository>()
));
```

Replace with:

```csharp
builder.Services.AddTransient<ScraperOrchestrator>();
```

This works because `ScraperOrchestrator` now has a constructor with all required services registered (`IEnumerable<ISourceScraper>`, `IFileDownloader`, `IRawDocumentRepository`, `IOptions<ScraperSettings>`, `ILogger<ScraperOrchestrator>`). DI resolves them automatically. `IRawDocumentRepository` is registered by `AddCosmosPersistence`; the CLI already requires Cosmos for the scraping path.

- [ ] **Step 7: Remove `using PinballWizard.Application.Provenance;` import if now unused**

```bash
grep -n "Provenance\|CatalogBuilder" src/PinballWizard.Cli/Program.cs
```

Delete the import if the namespace is no longer referenced.

- [ ] **Step 8: Build the CLI project**

```bash
dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 6: Delete `MigrateToRawCommand.cs`

**Files:**
- Delete: `src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs`

- [ ] **Step 1: Delete the file**

```bash
rm src/PinballWizard.Cli/Commands/MigrateToRawCommand.cs
```

- [ ] **Step 2: Check for remaining references**

```bash
grep -rn "MigrateToRawCommand" src/
```

Expected: no output.

- [ ] **Step 3: Build CLI project again**

```bash
dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 7: Archive data files

**Files:**
- Move: `data/metadata/catalog.json` → `data/archive/catalog.json`
- Move: `data/metadata/games.json` → `data/archive/games.json`

- [ ] **Step 1: Create archive directory and move files**

```bash
mkdir -p data/archive
git mv data/metadata/catalog.json data/archive/catalog.json
git mv data/metadata/games.json data/archive/games.json
```

If either file doesn't exist locally (never committed because it's in `.gitignore` or was empty), skip that file.

- [ ] **Step 2: Check for hardcoded paths referencing `data/metadata/catalog.json` or `data/metadata/games.json`**

```bash
grep -rn "data/metadata/catalog\|data/metadata/games" src/ tests/
```

Expected: no output (ScraperSettings uses configurable paths, not hardcoded ones).

---

### Task 8: Update `ScraperOrchestratorTests.cs`

**Files:**
- Modify: `tests/PinballWizard.Scraper.Tests/ScraperOrchestratorTests.cs`

The test helper `CreateOrchestrator` currently injects a real `CatalogBuilder`. After the change, `ScraperOrchestrator` requires `IRawDocumentRepository` instead.

- [ ] **Step 1: Update `CreateOrchestrator` helper**

Current:
```csharp
private ScraperOrchestrator CreateOrchestrator(
    IEnumerable<ISourceScraper> scrapers,
    ScraperSettings? settings = null)
{
    settings ??= new ScraperSettings { DataPath = _tempDir };
    var options = Options.Create(settings);
    var catalogBuilder = new CatalogBuilder(options, NullLogger<CatalogBuilder>.Instance);
    var httpClient = new HttpClient(new NoopHandler());
    var downloader = new FileDownloader(httpClient, options, NullLogger<FileDownloader>.Instance);

    return new ScraperOrchestrator(
        scrapers,
        downloader,
        catalogBuilder,
        options,
        NullLogger<ScraperOrchestrator>.Instance);
}
```

Replace with:
```csharp
private ScraperOrchestrator CreateOrchestrator(
    IEnumerable<ISourceScraper> scrapers,
    IRawDocumentRepository? rawDocRepo = null,
    ScraperSettings? settings = null)
{
    settings ??= new ScraperSettings { DataPath = _tempDir };
    var options = Options.Create(settings);
    var httpClient = new HttpClient(new NoopHandler());
    var downloader = new FileDownloader(httpClient, options, NullLogger<FileDownloader>.Instance);
    rawDocRepo ??= Substitute.For<IRawDocumentRepository>();

    return new ScraperOrchestrator(
        scrapers,
        downloader,
        rawDocRepo,
        options,
        NullLogger<ScraperOrchestrator>.Instance);
}
```

NSubstitute is already a test dependency (`using NSubstitute;` — add the using if not present).

- [ ] **Step 2: Remove tests that exercise the catalog-only code path**

The catalog-only path tested `NewDocuments`/`ExistingDocuments` counting (file-based deduplication) and `BuildCatalogAsync`. Search for tests using:
- `result.NewDocuments`
- `result.ExistingDocuments`
- `BuildCatalogAsync`
- `LoadCatalogAsync` (called from helpers like `LoadCatalogAsync(settings)`)
- `PrintStatusAsync`

Delete those test methods.

- [ ] **Step 3: Update tests that assert on `GamesDiscovered`**

If any test asserts `result.GamesDiscovered`, remove that assertion (the field is deleted).

- [ ] **Step 4: Add a using for NSubstitute if not present**

```csharp
using NSubstitute;
```

- [ ] **Step 5: Add two tests that verify the Cosmos path still works**

```csharp
[Fact]
public async Task ScrapeAsync_WithRawDocRepo_UpsertsEachLink()
{
    var rawRepo = Substitute.For<IRawDocumentRepository>();
    rawRepo.UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>())
        .Returns(ci => new RawDocumentRecord { DocumentId = "doc_test" });

    var scraper = new StubScraper("Manuals", [
        new ScrapedItem
        {
            DiscoveryUrl = "https://example.com",
            DiscoveryContext = "test",
            SourceType = SourceType.Manufacturer,
            Link = new DiscoveredLink
            {
                FileUrl = "https://example.com/manual.pdf",
                LinkText = "Manual"
            }
        }
    ]);

    var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);
    var result = await orch.ScrapeAsync();

    Assert.Equal(1, result.TotalLinks);
    Assert.Empty(result.Errors);
    await rawRepo.Received(1).UpsertRawAsync(
        Arg.Is<DocumentRecord>(d => d.Source.FileUrl == "https://example.com/manual.pdf"),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task ScrapeAsync_UpsertThrows_CapturesErrorAndContinues()
{
    var rawRepo = Substitute.For<IRawDocumentRepository>();
    rawRepo.UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>())
        .Throws(new InvalidOperationException("Cosmos unavailable"));

    var scraper = new StubScraper("Manuals", [
        new ScrapedItem
        {
            DiscoveryUrl = "https://example.com",
            DiscoveryContext = "test",
            SourceType = SourceType.Manufacturer,
            Link = new DiscoveredLink { FileUrl = "https://example.com/manual.pdf", LinkText = "Manual" }
        }
    ]);

    var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);
    var result = await orch.ScrapeAsync();

    Assert.Equal(0, result.TotalLinks);
    Assert.Single(result.Errors);
}
```

Note: `StubScraper`, `ScrapedItem`, `DiscoveredLink` are already defined in the test file. Add the `using NSubstitute;` import.

---

### Task 9: Update `IntegrationTests.cs`

**Files:**
- Modify: `tests/PinballWizard.Scraper.Tests/IntegrationTests.cs`

- [ ] **Step 1: Remove the `Host_CatalogBuilderAndDependenciesResolve` test**

Delete the entire test method (lines 89–99) — it asserts `CatalogBuilder` resolves, which it no longer will.

- [ ] **Step 2: Remove `CatalogBuilder` from the integration test host builder**

Find `builder.Services.AddTransient<CatalogBuilder>();` (line 308) and delete it.

- [ ] **Step 3: Check for catalog-seed logic in integration tests**

The integration test around line 194 writes a "seed catalog using the same JSON shape CatalogBuilder uses" — if this is part of a test for the file-based catalog path, delete that test. If there are integration tests for the Cosmos path that happened to share setup, keep the setup but remove the catalog-file lines.

- [ ] **Step 4: Build the test project**

```bash
dotnet build tests/PinballWizard.Scraper.Tests/PinballWizard.Scraper.Tests.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 10: Delete `CatalogBuilderTests.cs`

**Files:**
- Delete: `tests/PinballWizard.Scraper.Tests/CatalogBuilderTests.cs`

- [ ] **Step 1: Delete the file**

```bash
rm tests/PinballWizard.Scraper.Tests/CatalogBuilderTests.cs
```

- [ ] **Step 2: Build and run the full test suite**

```bash
dotnet test tests/PinballWizard.Scraper.Tests/PinballWizard.Scraper.Tests.csproj --no-build -- dotnet test tests/PinballWizard.Scraper.Tests/PinballWizard.Scraper.Tests.csproj
```

Actually run it in one step:

```bash
dotnet test tests/PinballWizard.Scraper.Tests/PinballWizard.Scraper.Tests.csproj
```

Expected: all tests pass. The count will be lower than the pre-retirement 1413 (CatalogBuilderTests alone was hundreds of tests). Any failing test must be fixed before proceeding.

---

### Task 11: Run the full solution test suite

- [ ] **Step 1: Run all tests**

```bash
dotnet test PinballWizard.slnx
```

Expected: 0 failures. Total count will be lower than 1721 (by the number of deleted tests) but must be 0 failing.

---

### Task 12: Commit

- [ ] **Step 1: Stage all changes**

```bash
git add -A
```

- [ ] **Step 2: Verify the staged files look correct**

```bash
git diff --cached --stat
```

Expected: deleted files are shown as deletions, modified files as modifications, no unintended changes.

- [ ] **Step 3: Commit**

```bash
git commit -m "feat(catalog) AB#259: retire CatalogBuilder — Cosmos write path is now the only path"
```

---

### Task 13: Pre-merge production count check (BLOCKING)

Do not merge this branch until this check is complete.

- [ ] **Step 1: Query production Cosmos scraped_documents_raw count**

Run against prod config (or ask Jim to run it):

```bash
dotnet run --project src/PinballWizard.Cli -- --status
```

Or query Cosmos directly:

```
SELECT VALUE COUNT(1) FROM c WHERE c.container = "scraped_documents_raw"
```

- [ ] **Step 2: Record the count in the PR description**

Example PR description line:
> Production `scraped_documents_raw` document count confirmed: 847 documents (matches `--migrate-to-raw` output from 2026-05-23 run).

---

## Self-Review

**Spec coverage:**
- Remove `CatalogBuilder` field + constructor param ✓ (Task 3 Step 1)
- Make `IRawDocumentRepository` required (non-optional) ✓ (Task 3 Step 1)
- Remove null-path branch in `ScrapeAsync` ✓ (Task 3 Step 2)
- Delete `DownloadAsync` + `BuildCatalogAsync` + `PrintStatusAsync` ✓ (Task 3 Step 3)
- Verify game record handling — OPDB sync is authoritative, scraped game data lives in `RawDocumentRecord.Game` ✓ (Task 2 Step 4)
- Delete `CatalogBuilder.cs` ✓ (Task 4)
- Delete `MigrateToRawCommand.cs` ✓ (Task 6)
- Update `Program.cs` DI + CLI options ✓ (Task 5)
- Archive `data/metadata/*.json` ✓ (Task 7)
- Update `ScraperOrchestratorTests.cs` ✓ (Task 8)
- Update `IntegrationTests.cs` ✓ (Task 9)
- Delete `CatalogBuilderTests.cs` ✓ (Task 10)
- Pre-merge prod count check ✓ (Task 13)

**No placeholders:** all code snippets are complete. The `StubScraper` + `ScrapedItem` + `DiscoveredLink` types referenced in Task 8 already exist in the test file.

**Type consistency:** `IRawDocumentRepository.UpsertRawAsync` takes `DocumentRecord` and returns `Task<RawDocumentRecord>` — the test stubs are consistent with the interface signature in `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs`.
