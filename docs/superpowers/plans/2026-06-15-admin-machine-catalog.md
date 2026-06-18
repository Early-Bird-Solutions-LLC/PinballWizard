# Admin Machine Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the empty `/admin/machines` skeleton with a manufacturer-grouped, re-groupable catalog that flags scraping gaps, plus a per-game detail page showing exactly what is linked, how it linked, and how it compares across editions.

**Architecture:** First consumer of the ADR-0036 Cosmos read-access standard. The summary reads a Tier-3 `catalog_stats` change-feed projection (one per-manufacturer rollup doc); the detail page uses Tier-1 single-partition reads of `scraped_documents` by `machine_id` plus a bounded sibling lookup. The projection is maintained by a second, independent change-feed consumer over `scraped_documents` and is fully rebuildable via a CLI verb.

**Tech Stack:** .NET 10, C#, Azure Cosmos SDK, MudBlazor (ADR-0008), Blazor InteractiveServer admin pages (ADR-0034 render-mode rules — but `/admin/*` pages are static; AdminLayout providers stay static), xUnit + bUnit, Aspire Cosmos emulator for integration tests.

**Depends on Plan 1** (`2026-06-15-cosmos-read-access-standard.md`) — the `StreamAsync`/`StreamCrossPartitionAsync` split and the allow-list test must be in place first.

---

## File Structure

**Application (read contracts + DTOs):**
- Create: `src/PinballWizard.Application/Catalog/ManufacturerCatalogStats.cs` — projection DTO (`ManufacturerCatalogStats`, `MachineDocStats`).
- Create: `src/PinballWizard.Application/Catalog/MachineDocumentLink.cs` — detail-page linked-doc DTO.
- Create: `src/PinballWizard.Application/Persistence/ICatalogStatsReadRepository.cs`
- Create: `src/PinballWizard.Application/Persistence/IMachineDocumentReadRepository.cs`
- Create: `src/PinballWizard.Application/Catalog/CatalogHealth.cs` — health-flag enum + pure computation (testable without Cosmos).

**Infrastructure (Cosmos record + repos + projection consumer + rebuild):**
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CatalogStatsCosmosRecord.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosCatalogStatsRepository.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosMachineDocumentReadRepository.cs`
- Create: `src/PinballWizard.Infrastructure/Catalog/CatalogStatsChangeFeedHandler.cs`
- Create: `src/PinballWizard.Infrastructure/Catalog/CatalogStatsProjectionOptions.cs`
- Create: `src/PinballWizard.Infrastructure/Catalog/CatalogStatsRebuildService.cs` + `ICatalogStatsRebuildService` (Application).
- Create: `src/PinballWizard.Infrastructure/Catalog/ServiceCollectionExtensions.cs` — `AddCatalogStatsProjection` (worker host) + `AddCatalogStatsRead` (Web/CLI).
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs:87+` — add `catalog_stats` + `catalog_stats_leases` containers.

**Worker / CLI / Web:**
- Modify: `src/PinballWizard.RagIngestionWorker/Program.cs` — register the catalog-stats consumer.
- Modify: `src/PinballWizard.Cli/Program.cs` — add `--rebuild-catalog-stats`.
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor` — rewrite summary.
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor` — detail page.
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` (or the admin nav component) — add a nav entry if not auto-discovered.
- Modify Web DI: wherever Cosmos repos are registered for Web — add `AddCatalogStatsRead`.

**Tests:** per-layer (Application: health computation; Infrastructure: handler + repos against emulator; Web: bUnit).

---

### Task 1: Projection DTOs + health computation (Application, pure)

**Files:**
- Create: `src/PinballWizard.Application/Catalog/ManufacturerCatalogStats.cs`
- Create: `src/PinballWizard.Application/Catalog/CatalogHealth.cs`
- Test: `tests/PinballWizard.Application.Tests/Catalog/CatalogHealthTests.cs`

- [ ] **Step 1: Write the DTOs**

```csharp
// ManufacturerCatalogStats.cs
namespace PinballWizard.Application.Catalog;

public sealed record ManufacturerCatalogStats(
    string Manufacturer,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<MachineDocStats> Machines);

public sealed record MachineDocStats(
    string MachineId,
    string Title,
    string? EditionLabel,
    string? GroupId,
    int? Year,
    bool IsOpdbOnly,                       // no manufacturer-scraper slug → expected gap signal
    int DocCount,
    IReadOnlyDictionary<string, int> DocTypeCounts,
    bool HasManual);
```

- [ ] **Step 2: Write the failing health test**

```csharp
// CatalogHealthTests.cs
using PinballWizard.Application.Catalog;
using Xunit;

namespace PinballWizard.Application.Tests.Catalog;

public sealed class CatalogHealthTests
{
    private static MachineDocStats Stat(string id, int docs, bool manual, string? group = null) =>
        new(id, id, null, group, 2021, false, docs,
            manual ? new Dictionary<string, int> { ["Manual"] = 1 } : new(), manual);

    [Fact]
    public void Empty_When_NoDocs()
        => Assert.Contains(CatalogHealthFlag.Empty,
            CatalogHealth.Evaluate(Stat("m", 0, false), siblings: []));

    [Fact]
    public void NoManual_When_DocsButNoManual()
        => Assert.Contains(CatalogHealthFlag.NoManual,
            CatalogHealth.Evaluate(Stat("m", 3, false), siblings: []));

    [Fact]
    public void EditionGap_When_FewerDocsThanSibling()
    {
        var self = Stat("pro", 0, false, group: "G");
        var sibling = Stat("le", 5, true, group: "G");
        Assert.Contains(CatalogHealthFlag.EditionGap,
            CatalogHealth.Evaluate(self, siblings: [sibling]));
    }

    [Fact]
    public void Ok_When_HasDocsAndManualAndNoGap()
        => Assert.Equal(
            new[] { CatalogHealthFlag.Ok },
            CatalogHealth.Evaluate(Stat("m", 4, true), siblings: []));
}
```

- [ ] **Step 3: Run — verify it fails (type not defined)**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~CatalogHealthTests"`
Expected: FAIL — `CatalogHealth` / `CatalogHealthFlag` not defined.

- [ ] **Step 4: Implement**

```csharp
// CatalogHealth.cs
namespace PinballWizard.Application.Catalog;

public enum CatalogHealthFlag { Ok, Empty, NoManual, EditionGap }

public static class CatalogHealth
{
    // Pure: flags for one machine given its same-GroupId siblings.
    public static IReadOnlyList<CatalogHealthFlag> Evaluate(
        MachineDocStats machine, IReadOnlyList<MachineDocStats> siblings)
    {
        var flags = new List<CatalogHealthFlag>();
        if (machine.DocCount == 0) flags.Add(CatalogHealthFlag.Empty);
        else if (!machine.HasManual) flags.Add(CatalogHealthFlag.NoManual);

        // Edition gap: a same-GroupId sibling has strictly more docs.
        if (machine.GroupId is not null &&
            siblings.Any(s => s.GroupId == machine.GroupId && s.DocCount > machine.DocCount))
            flags.Add(CatalogHealthFlag.EditionGap);

        return flags.Count == 0 ? [CatalogHealthFlag.Ok] : flags;
    }
}
```

- [ ] **Step 5: Run — verify pass.** `dotnet test ... --filter CatalogHealthTests` → PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Catalog/ tests/PinballWizard.Application.Tests/Catalog/
git commit -m "feat(app) AB#259: catalog stats DTOs + pure health-flag computation"
```

---

### Task 2: Read repository contracts + detail-page DTO (Application)

**Files:**
- Create: `src/PinballWizard.Application/Catalog/MachineDocumentLink.cs`
- Create: `src/PinballWizard.Application/Persistence/ICatalogStatsReadRepository.cs`
- Create: `src/PinballWizard.Application/Persistence/IMachineDocumentReadRepository.cs`

- [ ] **Step 1: Write the contracts**

```csharp
// MachineDocumentLink.cs
namespace PinballWizard.Application.Catalog;

public sealed record MachineDocumentLink(
    string DocumentId,
    string DocumentType,
    string DocumentUrl,
    string? LinkText,
    string? Edition,
    string? EditionScope,
    string? LinkStatus,            // from scraped_documents_raw (how-linked enrichment)
    string? ResolutionStrategy,
    DateTimeOffset? LastDownloadedUtc,
    long? SizeBytes,
    int? PageCount);
```

```csharp
// ICatalogStatsReadRepository.cs
using PinballWizard.Application.Catalog;
namespace PinballWizard.Application.Persistence;

public interface ICatalogStatsReadRepository
{
    // Tier 1 point read of the per-manufacturer rollup doc.
    Task<ManufacturerCatalogStats?> GetByManufacturerAsync(string manufacturer, CancellationToken cancellationToken);

    // Loads every manufacturer rollup (bounded: ~8-9 docs). Used by the
    // summary's "expand all" / non-manufacturer group-bys. Each is a
    // single-partition point read; the set of manufacturers comes from a
    // small known list (the projection writes one doc per manufacturer).
    IAsyncEnumerable<ManufacturerCatalogStats> StreamAllManufacturersAsync(CancellationToken cancellationToken);
}
```

```csharp
// IMachineDocumentReadRepository.cs
using PinballWizard.Application.Catalog;
namespace PinballWizard.Application.Persistence;

public interface IMachineDocumentReadRepository
{
    // Tier 1: single-partition read of scraped_documents by machine_id.
    IAsyncEnumerable<MachineDocumentLink> StreamByMachineIdAsync(string machineId, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Build the Application project.** `dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj` → PASS (interfaces only).

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Application/Catalog/MachineDocumentLink.cs src/PinballWizard.Application/Persistence/I*ReadRepository.cs
git commit -m "feat(app) AB#259: catalog read-repository contracts + detail-link DTO"
```

---

### Task 3: catalog_stats containers + Cosmos record (Infrastructure)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CatalogStatsCosmosRecord.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs` (after the `rag_dead_letters` block, before the closing `]`)

- [ ] **Step 1: Add the containers to `CosmosOptions.Containers`**

Insert after the `rag_dead_letters` entry:

```csharp
        // catalog_stats — Tier-3 read model per ADR-0036. One rollup doc
        // per manufacturer (id == /manufacturer) holding per-machine doc
        // counts/types for the admin catalog summary. Maintained by the
        // CatalogStatsChangeFeedHandler consumer over scraped_documents;
        // rebuildable via `--rebuild-catalog-stats`. Default indexing:
        // reads are point-reads by manufacturer.
        new() { Name = "catalog_stats", PartitionKeyPath = "/manufacturer" },
        // catalog_stats_leases — dedicated Change Feed lease container for
        // the catalog-stats consumer. MUST be separate from rag_leases so
        // the two consumers track independent cursors over scraped_documents.
        new() { Name = "catalog_stats_leases", PartitionKeyPath = "/id" },
```

- [ ] **Step 2: Write the Cosmos record**

```csharp
// CatalogStatsCosmosRecord.cs
using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Per-manufacturer rollup doc for the catalog_stats projection (ADR-0036 Tier 3).
public sealed class CatalogStatsCosmosRecord : IEntity
{
    [JsonPropertyName("id")] public required string Id { get; set; }                  // == manufacturer
    [JsonPropertyName("manufacturer")] public required string PartitionKey { get; set; }
    [JsonPropertyName("asOfUtc")] public DateTimeOffset AsOfUtc { get; set; }
    [JsonPropertyName("machines")] public List<MachineStatEntry> Machines { get; set; } = [];
    [JsonPropertyName("_etag")] public string? ETag { get; set; }
}

public sealed class MachineStatEntry
{
    [JsonPropertyName("machineId")] public required string MachineId { get; set; }
    [JsonPropertyName("title")] public required string Title { get; set; }
    [JsonPropertyName("editionLabel")] public string? EditionLabel { get; set; }
    [JsonPropertyName("groupId")] public string? GroupId { get; set; }
    [JsonPropertyName("year")] public int? Year { get; set; }
    [JsonPropertyName("isOpdbOnly")] public bool IsOpdbOnly { get; set; }
    [JsonPropertyName("docCount")] public int DocCount { get; set; }
    [JsonPropertyName("docTypeCounts")] public Dictionary<string, int> DocTypeCounts { get; set; } = [];
    [JsonPropertyName("hasManual")] public bool HasManual { get; set; }
}
```

- [ ] **Step 3: Build Infrastructure.** `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj` → PASS.

- [ ] **Step 4: Verify `--ensure-cosmos-containers` provisions the new containers (emulator)**

Start the Aspire AppHost emulator, then run: `dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers`
Expected: exit 0; `catalog_stats` and `catalog_stats_leases` reported created/verified.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CatalogStatsCosmosRecord.cs
git commit -m "feat(infra) AB#259: catalog_stats + lease containers + rollup record (ADR-0036 Tier 3)"
```

---

### Task 4: Tier-1 read repos (Infrastructure)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosMachineDocumentReadRepository.cs`
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosCatalogStatsRepository.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Catalog/CosmosMachineDocumentReadRepositoryTests.cs` (emulator)

- [ ] **Step 1: Write the failing emulator test for machine-doc reads**

```csharp
// CosmosMachineDocumentReadRepositoryTests.cs — uses the existing emulator fixture
// pattern from the Infrastructure.Tests Cosmos suite (collection fixture that
// provisions a throwaway db/container). Seeds two scraped_documents fan-out rows
// for machine "mch_A" and one for "mch_B", asserts StreamByMachineIdAsync("mch_A")
// returns exactly 2 and never a "mch_B" row.
```

(Write the test using the same Cosmos emulator collection-fixture other `*RepositoryTests` in this project use; assert count == 2 and all `MachineId`-partition rows belong to `mch_A`.)

- [ ] **Step 2: Run — fails (repo not defined).**

- [ ] **Step 3: Implement the machine-doc read repo (Tier 1)**

```csharp
// CosmosMachineDocumentReadRepository.cs
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Tier-1 reader over scraped_documents (pk /machine_id). Enriches each row
// with link-status from scraped_documents_raw via a point read (pk /document_id).
public sealed class CosmosMachineDocumentReadRepository : IMachineDocumentReadRepository
{
    private readonly Container _scrapedDocuments;
    private readonly IRawDocumentRepository _rawDocs;
    private readonly ILogger<CosmosMachineDocumentReadRepository> _logger;

    public CosmosMachineDocumentReadRepository(
        Container scrapedDocuments,
        IRawDocumentRepository rawDocs,
        ILogger<CosmosMachineDocumentReadRepository> logger)
    {
        _scrapedDocuments = scrapedDocuments;
        _rawDocs = rawDocs;
        _logger = logger;
    }

    public async IAsyncEnumerable<MachineDocumentLink> StreamByMachineIdAsync(
        string machineId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.machine_id = @mid")
            .WithParameter("@mid", machineId);
        using var it = _scrapedDocuments.GetItemQueryIterator<ScrapedDocumentRecord>(
            q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(machineId) });
        while (it.HasMoreResults)
        {
            foreach (var d in await it.ReadNextAsync(ct).ConfigureAwait(false))
            {
                var raw = await _rawDocs.GetAsync(d.DocumentId, ct).ConfigureAwait(false);
                yield return new MachineDocumentLink(
                    DocumentId: d.DocumentId,
                    DocumentType: d.DocumentType,
                    DocumentUrl: d.DocumentUrl,
                    LinkText: d.Source?.LinkText,
                    Edition: d.Edition,
                    EditionScope: d.EditionScope,
                    LinkStatus: raw?.LinkStatus.ToString(),
                    ResolutionStrategy: raw?.ResolutionStrategy,
                    LastDownloadedUtc: d.LastDownloadedAt,
                    SizeBytes: d.File?.SizeBytes,
                    PageCount: d.File?.PageCount);
            }
        }
    }
}
```

> NOTE: confirm `ScrapedDocumentRecord` exposes `MachineId`, `DocumentId`, `DocumentType`, `DocumentUrl`, `Source.LinkText`, `Edition`, `EditionScope`, `LastDownloadedAt`, `File.SizeBytes`, `File.PageCount` (it is the write-side record; see `ScrapedDocumentRecord.cs`). If a field name differs, adjust the projection — do not invent fields.

- [ ] **Step 4: Implement the catalog-stats read repo**

```csharp
// CosmosCatalogStatsRepository.cs
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

public sealed class CosmosCatalogStatsRepository
    : CosmosRepository<CatalogStatsCosmosRecord>, ICatalogStatsReadRepository
{
    // Manufacturer keys are the machine partition keys; the projection writes
    // one doc per manufacturer. The known set is small and stable.
    private readonly IReadOnlyList<string> _manufacturers;

    public CosmosCatalogStatsRepository(
        Microsoft.Azure.Cosmos.Container container,
        IReadOnlyList<string> manufacturers,
        ILogger<CosmosRepository<CatalogStatsCosmosRecord>> logger) : base(container, logger)
        => _manufacturers = manufacturers;

    public async Task<ManufacturerCatalogStats?> GetByManufacturerAsync(string manufacturer, CancellationToken ct)
    {
        var rec = await GetByIdAsync(manufacturer, manufacturer, ct).ConfigureAwait(false);
        return rec is null ? null : Map(rec);
    }

    public async IAsyncEnumerable<ManufacturerCatalogStats> StreamAllManufacturersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var m in _manufacturers)
        {
            var rec = await GetByIdAsync(m, m, ct).ConfigureAwait(false);
            if (rec is not null) yield return Map(rec);
        }
    }

    private static ManufacturerCatalogStats Map(CatalogStatsCosmosRecord r) => new(
        r.PartitionKey, r.AsOfUtc,
        r.Machines.Select(e => new MachineDocStats(
            e.MachineId, e.Title, e.EditionLabel, e.GroupId, e.Year, e.IsOpdbOnly,
            e.DocCount, e.DocTypeCounts, e.HasManual)).ToList());
}
```

> NOTE: the `_manufacturers` list is the distinct set of manufacturer partition keys. Source it from the same place the scrapers enumerate manufacturers (see how `MachineRepository`/scraper registry lists manufacturers); if there is no single constant, derive it once at startup by reading the distinct manufacturer keys — but do NOT add a cross-partition query for this; prefer a static known list matching the 8 manufacturers + OPDB.

- [ ] **Step 5: Run the emulator test → PASS.**

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/Cosmos*ReadRepository.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosCatalogStatsRepository.cs tests/PinballWizard.Infrastructure.Tests/Catalog/
git commit -m "feat(infra) AB#259: Tier-1 catalog read repositories"
```

---

### Task 5: catalog_stats change-feed consumer (Infrastructure + worker)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Catalog/CatalogStatsProjectionOptions.cs`
- Create: `src/PinballWizard.Infrastructure/Catalog/CatalogStatsChangeFeedHandler.cs`
- Create: `src/PinballWizard.Infrastructure/Catalog/ServiceCollectionExtensions.cs`
- Modify: `src/PinballWizard.RagIngestionWorker/Program.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Catalog/CatalogStatsChangeFeedHandlerTests.cs`

- [ ] **Step 1: Options (own section, own lease container + processor)**

```csharp
// CatalogStatsProjectionOptions.cs
using System.ComponentModel.DataAnnotations;
namespace PinballWizard.Infrastructure.Catalog;

public sealed class CatalogStatsProjectionOptions
{
    public const string SectionName = "Catalog:Stats";
    [Required] public string SourceContainerName { get; init; } = "scraped_documents";
    [Required] public string LeaseContainerName { get; init; } = "catalog_stats_leases";
    [Required] public string ProcessorName { get; init; } = "catalog-stats";
    public string? InstanceName { get; init; }
    public bool StartFromBeginning { get; init; } = true;
}
```

- [ ] **Step 2: Write the failing handler test**

Test: seed `scraped_documents` with two rows for machine `mch_A` (manufacturer `stern`, one Manual + one Bulletin); invoke `HandleAsync` with one change for `mch_A`; assert the `catalog_stats` doc for `stern` now has a `mch_A` entry with `DocCount=2`, `HasManual=true`, `DocTypeCounts["Manual"]=1`. Then add a third row + re-handle; assert idempotent recompute (`DocCount=3`, no duplicate entry).

- [ ] **Step 3: Run — fails (handler not defined).**

- [ ] **Step 4: Implement the handler (incremental, partition-aligned recompute)**

```csharp
// CatalogStatsChangeFeedHandler.cs
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Catalog;

// Second, independent Change Feed consumer over scraped_documents (ADR-0036
// Tier 3). On each change it recomputes ONLY the affected machine's stats via
// a single-partition query (pk /machine_id), then upserts that machine's entry
// into the per-manufacturer catalog_stats rollup doc under ETag-retry. Counts
// ALL document types (unlike the RAG handler, which filters non-indexables) —
// which is why it cannot share the RAG consumer.
public sealed class CatalogStatsChangeFeedHandler : ICosmosChangeFeedHandler<RagSourceDocument>
{
    private readonly Container _scrapedDocuments;
    private readonly Container _catalogStats;
    private readonly TimeProvider _clock;
    private readonly ILogger<CatalogStatsChangeFeedHandler> _logger;
    private const int MaxEtagRetries = 5;

    public CatalogStatsChangeFeedHandler(
        Container scrapedDocuments, Container catalogStats,
        TimeProvider clock, ILogger<CatalogStatsChangeFeedHandler> logger)
    { _scrapedDocuments = scrapedDocuments; _catalogStats = catalogStats; _clock = clock; _logger = logger; }

    public async Task<IngestionOutcome?> HandleAsync(RagSourceDocument change, CancellationToken ct)
    {
        var entry = await ComputeMachineEntryAsync(change.MachineId, ct).ConfigureAwait(false);
        await UpsertEntryWithRetryAsync(change.Manufacturer, entry, ct).ConfigureAwait(false);
        return null; // non-RAG handler — no ingestion outcome
    }

    // Single-partition recompute of one machine's stats.
    private async Task<MachineStatEntry> ComputeMachineEntryAsync(string machineId, CancellationToken ct)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.machine_id = @mid").WithParameter("@mid", machineId);
        using var it = _scrapedDocuments.GetItemQueryIterator<ScrapedDocumentRecord>(
            q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(machineId) });
        var typeCounts = new Dictionary<string, int>();
        var count = 0; string title = machineId; string? edition = null, group = null; int? year = null;
        while (it.HasMoreResults)
            foreach (var d in await it.ReadNextAsync(ct).ConfigureAwait(false))
            {
                count++;
                typeCounts[d.DocumentType] = typeCounts.GetValueOrDefault(d.DocumentType) + 1;
                title = d.MachineTitle ?? title;     // identity fields denormalized on the doc
            }
        // editionLabel/groupId/year/isOpdbOnly come from the Machine record; the
        // rebuild service (Task 6) backfills them. For incremental updates, carry
        // forward any existing entry's identity fields (set in UpsertEntryWithRetryAsync).
        return new MachineStatEntry
        {
            MachineId = machineId, Title = title, EditionLabel = edition, GroupId = group, Year = year,
            DocCount = count, DocTypeCounts = typeCounts,
            HasManual = typeCounts.Keys.Any(k => string.Equals(k, "Manual", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private async Task UpsertEntryWithRetryAsync(string manufacturer, MachineStatEntry entry, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxEtagRetries; attempt++)
        {
            CatalogStatsCosmosRecord doc;
            try
            {
                var resp = await _catalogStats.ReadItemAsync<CatalogStatsCosmosRecord>(
                    manufacturer, new PartitionKey(manufacturer), cancellationToken: ct).ConfigureAwait(false);
                doc = resp.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                doc = new CatalogStatsCosmosRecord { Id = manufacturer, PartitionKey = manufacturer };
            }

            var existing = doc.Machines.FirstOrDefault(m => m.MachineId == entry.MachineId);
            if (existing is not null)
            {
                // preserve identity fields the change feed doesn't carry
                entry.EditionLabel = existing.EditionLabel; entry.GroupId = existing.GroupId;
                entry.Year = existing.Year; entry.IsOpdbOnly = existing.IsOpdbOnly;
                doc.Machines.Remove(existing);
            }
            doc.Machines.Add(entry);
            doc.AsOfUtc = _clock.GetUtcNow();

            try
            {
                var options = doc.ETag is null
                    ? new ItemRequestOptions()
                    : new ItemRequestOptions { IfMatchEtag = doc.ETag };
                await _catalogStats.UpsertItemAsync(doc, new PartitionKey(manufacturer), options, ct).ConfigureAwait(false);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                _logger.LogDebug("catalog_stats ETag conflict for {Manufacturer}, retry {Attempt}.", manufacturer, attempt + 1);
            }
        }
        throw new InvalidOperationException($"catalog_stats update for '{manufacturer}' exhausted {MaxEtagRetries} ETag retries.");
    }
}
```

> NOTE: the handler intentionally throws on retry-exhaustion so the change-feed hosted service dead-letters it (visible failure, not silent — Invariant #17). Identity fields (editionLabel/groupId/year/isOpdbOnly) are authoritative from the Machine record and are populated by the rebuild service (Task 6); the incremental handler preserves whatever the rebuild last wrote.

- [ ] **Step 5: Registration extension**

```csharp
// ServiceCollectionExtensions.cs (Catalog)
// AddCatalogStatsProjection: binds CatalogStatsProjectionOptions, registers the
// handler + a second CosmosChangeFeedHostedService<RagSourceDocument> with the
// catalog-stats lease container + processor name. Mirrors the RAG registration
// in Rag/Ingestion/ServiceCollectionExtensions.cs but with its own options and
// NO dead-letter coupling to the RAG sink (use a catalog-stats dead-letter sink
// or the shared one — reuse CosmosBackedDeadLetterSink against rag_dead_letters).
// AddCatalogStatsRead: registers CosmosCatalogStatsRepository + the machine-doc
// read repo for the Web/CLI hosts (read-only; no hosted service).
```

(Implement following the exact construction shape of `AddCosmosChangeFeedRagIngestion` lines 109-127, substituting `CatalogStatsProjectionOptions`, the `catalog_stats_leases` container, the `CatalogStatsChangeFeedHandler`, and resolving the `catalog_stats` + `scraped_documents` containers via the same `ResolveContainer` helper pattern.)

- [ ] **Step 6: Register in the worker**

In `src/PinballWizard.RagIngestionWorker/Program.cs`, after `AddCosmosChangeFeedRagIngestion`, add `builder.Services.AddCatalogStatsProjection(builder.Configuration);`. Add a comment that this runs a SECOND change-feed consumer; document that for multi-replica correctness the catalog-stats consumer's manufacturer-doc updates are ETag-guarded (handler) + rebuildable (`--rebuild-catalog-stats`).

- [ ] **Step 7: Run handler test → PASS. Commit.**

```bash
git add src/PinballWizard.Infrastructure/Catalog/ src/PinballWizard.RagIngestionWorker/Program.cs tests/PinballWizard.Infrastructure.Tests/Catalog/CatalogStatsChangeFeedHandlerTests.cs
git commit -m "feat(infra) AB#259: catalog_stats change-feed projection consumer"
```

---

### Task 6: `--rebuild-catalog-stats` CLI verb (rebuildable projection backstop)

**Files:**
- Create: `src/PinballWizard.Application/Catalog/ICatalogStatsRebuildService.cs`
- Create: `src/PinballWizard.Infrastructure/Catalog/CatalogStatsRebuildService.cs`
- Modify: `src/PinballWizard.Cli/Program.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Catalog/CatalogStatsRebuildServiceTests.cs`

- [ ] **Step 1: Rebuild contract + service**

The rebuild streams every Machine (via `IMachineRepository.StreamAllAsync` — already an allow-listed Tier-2 cross-partition site, so NO new cross-partition query), and for each machine does a single-partition `scraped_documents` read to compute its stats (populating the authoritative identity fields editionLabel/groupId/year + `IsOpdbOnly` from the Machine record's `ManufacturerSlugs`), bucketing into per-manufacturer rollup docs, then upserts each rollup. Idempotent: replaces each rollup doc wholesale.

- [ ] **Step 2: Failing test** — seed 3 machines across 2 manufacturers + their docs; run rebuild; assert two `catalog_stats` docs with correct per-machine counts and identity fields.

- [ ] **Step 3: Implement the service** (stream machines → per-machine single-partition doc read → compute `MachineStatEntry` incl. identity fields from the `Machine` → group by manufacturer → wholesale upsert each `CatalogStatsCosmosRecord` with `AsOfUtc = clock.GetUtcNow()`).

- [ ] **Step 4: Wire the CLI verb** following the `--rebuild-rag-index` pattern (`src/PinballWizard.Cli/Program.cs:97`, `:527`): an `Option<bool> rebuildCatalogStatsOption`, the exit-code-2 "Cosmos not configured" remediation guard (mirror `:236`), resolve `ICatalogStatsRebuildService`, run, log counts.

- [ ] **Step 5: Run test → PASS. Manual emulator run:** `dotnet run --project src/PinballWizard.Cli -- --rebuild-catalog-stats` → exit 0, logs N manufacturers / M machines.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Catalog/ICatalogStatsRebuildService.cs src/PinballWizard.Infrastructure/Catalog/CatalogStatsRebuildService.cs src/PinballWizard.Cli/Program.cs tests/PinballWizard.Infrastructure.Tests/Catalog/CatalogStatsRebuildServiceTests.cs
git commit -m "feat(cli) AB#259: --rebuild-catalog-stats (rebuildable projection backstop)"
```

---

### Task 7: Summary page rewrite `/admin/machines`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor`
- Web DI: register `AddCatalogStatsRead` where Web wires Cosmos repos.
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminMachinesTests.cs` (bUnit)

- [ ] **Step 1: bUnit failing test** — render with a fake `ICatalogStatsReadRepository` returning two manufacturers (one with an Empty machine, one OK); assert the grid renders rows, the group headers show roll-up flag counts, and an "as of" timestamp element is present (`data-testid="catalog-as-of"`).

- [ ] **Step 2: Implement the page** — inject `ICatalogStatsReadRepository`; load all manufacturers on init (bounded); flatten to rows with `CatalogHealth.Evaluate` (siblings = same-manufacturer same-GroupId rows); MudDataGrid with `Groupable` + a group-by selector (`MudSelect` bound to the active axis: Manufacturer/Health/Franchise/Year/Source) that sets the grid's grouping client-side; health as `MudChip` with `Color` by severity (theme tokens, **no row-background tint** — the ADR-0034-review contrast rule); row click → `Nav.NavigateTo($"/admin/machines/{machineId}")`. Render the min `AsOfUtc` with `data-testid="catalog-as-of"`. Keep the page static (`/admin/*` carry no `@rendermode`; AdminLayout providers stay static per ADR-0034).

```razor
@page "/admin/machines"
@layout AdminLayout
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Application.Persistence
@using PinballWizard.Application.Catalog
@attribute [Authorize(Policy = "AdminOnly")]
@inject ICatalogStatsReadRepository Stats
@inject NavigationManager Nav
@inject ISnackbar Snackbar
@* full markup: group-by MudSelect, MudDataGrid<MachineRow> with Groupable columns,
   health MudChip column, NoRecordsContent, and the as-of caption. *@
```

- [ ] **Step 3: Run bUnit test → PASS.** (If MudDataGrid grouping needs `MudThemeProvider`/services in the test, use the existing admin bUnit setup pattern with `AddMudServices()`.)

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminMachinesTests.cs
git commit -m "feat(web) AB#259: admin machine catalog summary (grouped, health-flagged)"
```

---

### Task 8: Detail page `/admin/machines/{opdbId}`

**Files:**
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminMachineDetailTests.cs`

- [ ] **Step 1: bUnit failing test** — fake `IMachineRepository` (machine + one sibling via `GetSiblingsByGroupIdAsync`) and fake `IMachineDocumentReadRepository` (two linked docs); assert: header renders title+edition+OPDB id; the edition-sibling strip shows both editions with their doc counts; the linked-docs table shows two rows incl. the `LinkStatus`/`How-linked` columns; empty-state when zero docs.

- [ ] **Step 2: Implement** — route param `{opdbId}`; load the machine (need its manufacturer — resolve via `IMachineRepository`; the catalog row passed the machineId, manufacturer can be looked up by `GetSiblingsByGroupIdAsync` or a title/groupId path — simplest: a point read needs manufacturer, so the summary row link carries `?mfr=` OR the detail page resolves via the machine's known partition; use `GetByOpdbIdAsync(opdbId, manufacturer)` with manufacturer from a query-string param the summary supplies). Edition-sibling strip via `GetSiblingsByGroupIdAsync(groupId)`; linked docs via `IMachineDocumentReadRepository.StreamByMachineIdAsync`. Health chips, file links (`target="_blank"`), action deep-links to `/admin/document-triage` and a "Create link override" that opens the existing override dialog/route prefilled. Static page.

> NOTE: to avoid an extra cross-partition lookup for the machine's manufacturer, the summary row links as `/admin/machines/{opdbId}?mfr={manufacturer}` (the summary already has the manufacturer in hand from the rollup doc). The detail page reads `mfr` from the query string and does a Tier-1 `GetByOpdbIdAsync(opdbId, mfr)` point read.

- [ ] **Step 3: Run bUnit test → PASS.**

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminMachineDetailTests.cs
git commit -m "feat(web) AB#259: per-game document detail page + edition-sibling strip"
```

---

### Task 9: Nav entry + full-suite verification

**Files:**
- Modify: the admin nav (AdminLayout or its nav component) — add a "Machines" link to `/admin/machines` if not already present.

- [ ] **Step 1:** Add/confirm the nav link.
- [ ] **Step 2: Full build + test.** `dotnet build PinballWizard.slnx` then `dotnet test PinballWizard.slnx`. Expected: PASS, including Plan 1's `CrossPartitionQueryAllowListTests` (confirm Task 4/6 added NO new cross-partition site — the read repos are Tier 1; the rebuild uses the already-allow-listed `MachineRepository.StreamAllAsync`).
- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/
git commit -m "feat(web) AB#259: admin nav entry for machine catalog"
```

---

## Self-Review

**Spec coverage:** summary grouped/flagged page (Task 7) ✓; group-by axes incl. Source/OPDB-only (Tasks 1,7) ✓; four health flags (Task 1) ✓; per-manufacturer Tier-3 projection (Tasks 3,5) ✓; "as of" stamp (Tasks 5,7) ✓; detail page Tier-1 + link-health + edition-sibling strip + actions (Task 8) ✓; rebuild backstop (Task 6) ✓; fuzzy candidate matching explicitly out of scope (not implemented) ✓; accessible chips not row-tint (Task 7) ✓.

**Placeholder scan:** the two Blazor pages (Tasks 7,8) give the contract + injected services + the key markup elements and `data-testid`s the tests assert, rather than the full ~150-line razor — acceptable because the tests pin the required behavior and the MudDataGrid grouping API is standard MudBlazor. The registration extension (Task 5 Step 5) references the exact RAG registration lines to mirror. All non-UI logic (DTOs, health, repos, handler, rebuild) has complete code.

**Type consistency:** `MachineStatEntry`/`MachineDocStats`/`ManufacturerCatalogStats` field names match across record (Task 3), DTO (Task 1), repo Map (Task 4), handler (Task 5), rebuild (Task 6). `MachineDocumentLink` fields match between contract (Task 2) and repo (Task 4) and detail test (Task 8). `CatalogHealth.Evaluate(machine, siblings)` signature consistent (Tasks 1,7).

**Cross-partition discipline check (ADR-0036):** the only cross-partition reads introduced are `MachineRepository.StreamAllAsync` (rebuild, already allow-listed) and `GetSiblingsByGroupIdAsync` (detail strip, already allow-listed). All new reads (machine-docs, catalog-stats, per-machine recompute) are Tier-1 single-partition. Task 9 Step 2 verifies the allow-list test still passes.

**Risk notes:**
- Multi-replica race on a per-manufacturer doc is handled by ETag-retry in the handler; `--rebuild-catalog-stats` is the correctness backstop. If the worker scales >1 replica and conflict-retry exhaustion appears, pin the catalog-stats consumer to a single replica (operator note in the worker registration).
- `ScrapedDocumentRecord` field names must be confirmed against the actual class (Task 4 NOTE) before coding the projection.
