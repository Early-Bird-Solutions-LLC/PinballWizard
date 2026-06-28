# Documents Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a public `/documents` browse surface and `/documents/{id}` detail page (each mirrored at `/admin/documents[/{id}]`), backed by game + manufacturer filtering with URL query params for deep linking.

**Architecture:** Shared `DocumentList.razor` and `DocumentDetail.razor` components in `Components/Shared/` are parameterized by `IsAdmin`; thin `@page` wrappers in `Components/Pages/` and `Components/Pages/Admin/` supply routes, query params, and auth attributes. The data layer adds `manufacturer` denormalization to the ingestion pipeline and two new repository methods with a Cosmos cross-partition query.

**Tech Stack:** Blazor Server (InteractiveServer), MudBlazor 9, `AppDataGrid` / `AppPageHeader` / `AppEmptyState` / `AppErrorAlert` / `AppStatusChip` (all in `Components/Shared/`), `Microsoft.Azure.Cosmos`, NSubstitute + bUnit for tests.

## Global Constraints

- All scraper `Manufacturer` string values must exactly match: `"Stern"`, `"Jersey Jack"`, `"Spooky"`, `"American Pinball"`, `"Pinball Brothers"`, `"Barrels of Fun"`, `"Multimorphic"`, `"Chicago Gaming"`.
- No bare `HttpClient.GetAsync` in scraper code; no new Cosmos cross-partition queries without an allow-list entry.
- All commits authored as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. No Claude attribution trailer.
- Run `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"` as the test gate before each commit.
- Branch: `feat/documents-page`.

---

## File Map

| Action | Path | Purpose |
|---|---|---|
| Modify | `src/PinballWizard.Core/Scraping/ISourceScraper.cs` | Add `string Manufacturer { get; }` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Stern/ManualsScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Stern/GamePageScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Stern/ServiceBulletinScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Jjp/JjpProductScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Jjp/JjpSupportDocScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Spooky/SpookyGamePageScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Spooky/SpookySupportPageScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Ap/ApGamePageScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Ap/ApBulletinScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/PbGamePageScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/PbGamePageDocumentScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/BarrelsOfFun/BofProductScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/Multimorphic/MultimorphicProductScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Scraping/ChicagoGaming/CgcGamePageScraper.cs` | Implement `Manufacturer` |
| Modify | `src/PinballWizard.Core/Models/DocumentRecord.cs` | Add `string? Manufacturer` |
| Modify | `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentCosmosRecord.cs` | Add `manufacturer` field |
| Modify | `src/PinballWizard.Application/ScraperOrchestrator.cs` | Set `record.Manufacturer` from scraper |
| Modify | `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` | Map `Manufacturer` in upsert; add `StreamDocumentsAsync` + `GetDocumentDetailAsync` |
| Create | `src/PinballWizard.Application/Documents/DocumentListItem.cs` | List page DTO |
| Create | `src/PinballWizard.Application/Documents/DocumentDetailRecord.cs` | Detail page DTO |
| Modify | `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs` | Add two new method signatures |
| Modify | `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs` | Allow `CosmosRawDocumentRepository.cs` cross-partition query |
| Create | `src/PinballWizard.Web/Components/Shared/DocumentList.razor` | Shared list component |
| Create | `src/PinballWizard.Web/Components/Shared/DocumentDetail.razor` | Shared detail component |
| Create | `src/PinballWizard.Web/Components/Pages/Documents.razor` | Public list page |
| Create | `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor` | Public detail page |
| Create | `src/PinballWizard.Web/Components/Pages/Admin/AdminDocuments.razor` | Admin list page |
| Create | `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor` | Admin detail page |
| Modify | `src/PinballWizard.Web/Components/Theming/BrandHeader.razor` | Add Documents nav button |
| Modify | `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` | Add Documents nav link |
| Modify | `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor` | Link doc titles to `/documents/{id}` |
| Create | `tests/PinballWizard.Web.Tests/Components/DocumentListTests.cs` | bUnit tests for list component |
| Create | `tests/PinballWizard.Web.Tests/Components/DocumentDetailTests.cs` | bUnit tests for detail component |

---

### Task 1: Add `Manufacturer` to `ISourceScraper` and all 14 scrapers

**Files:**
- Modify: `src/PinballWizard.Core/Scraping/ISourceScraper.cs`
- Modify: all 14 scraper files listed in the File Map above

**Interfaces:**
- Produces: `ISourceScraper.Manufacturer` — consumed by Task 2 (ScraperOrchestrator wiring)

- [ ] **Step 1: Add the property to the interface**

In `ISourceScraper.cs`, add after `string Name { get; }`:

```csharp
/// <summary>Canonical manufacturer name, e.g. "Stern", "Jersey Jack".</summary>
string Manufacturer { get; }
```

- [ ] **Step 2: Implement in all scrapers**

Add `public string Manufacturer => "<value>";` to each scraper class. Exact values:

| Folder / File | `Manufacturer` value |
|---|---|
| `Stern/ManualsScraper.cs` | `"Stern"` |
| `Stern/GamePageScraper.cs` | `"Stern"` |
| `Stern/ServiceBulletinScraper.cs` | `"Stern"` |
| `Jjp/JjpProductScraper.cs` | `"Jersey Jack"` |
| `Jjp/JjpSupportDocScraper.cs` | `"Jersey Jack"` |
| `Spooky/SpookyGamePageScraper.cs` | `"Spooky"` |
| `Spooky/SpookySupportPageScraper.cs` | `"Spooky"` |
| `Ap/ApGamePageScraper.cs` | `"American Pinball"` |
| `Ap/ApBulletinScraper.cs` | `"American Pinball"` |
| `PinballBrothers/PbGamePageScraper.cs` | `"Pinball Brothers"` |
| `PinballBrothers/PbGamePageDocumentScraper.cs` | `"Pinball Brothers"` |
| `BarrelsOfFun/BofProductScraper.cs` | `"Barrels of Fun"` |
| `Multimorphic/MultimorphicProductScraper.cs` | `"Multimorphic"` |
| `ChicagoGaming/CgcGamePageScraper.cs` | `"Chicago Gaming"` |

**Note:** `Twip/` and `Kineticist/` folders exist but are NOT `ISourceScraper` implementations — grep to confirm before changing them. Any scraper in those folders that DOES implement `ISourceScraper` must also get a `Manufacturer` property; the build will fail to compile if any implementor is missed.

- [ ] **Step 3: Verify the build compiles**

```
dotnet build src/PinballWizard.Core/PinballWizard.Core.csproj
dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj
```

Expected: no errors. A compile error means a scraper implementation was missed.

- [ ] **Step 4: Run SourceAliasContractTests to confirm nothing broke**

```
dotnet test PinballWizard.slnx --filter "FullyQualifiedName~SourceAliasContract" -v minimal
```

Expected: all pass (adding a property to the interface doesn't affect the alias contract).

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Core/Scraping/ISourceScraper.cs src/PinballWizard.Infrastructure/Scraping/
git commit -m "feat(scraping) add Manufacturer property to ISourceScraper + all 14 scrapers"
```

---

### Task 2: Denormalize `manufacturer` into `DocumentRecord` + `RawDocumentCosmosRecord` + pipeline wiring

**Files:**
- Modify: `src/PinballWizard.Core/Models/DocumentRecord.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentCosmosRecord.cs`
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs`

**Interfaces:**
- Consumes: `ISourceScraper.Manufacturer` from Task 1
- Produces: `DocumentRecord.Manufacturer`, `RawDocumentCosmosRecord.Manufacturer` — consumed by Tasks 4 and 6

- [ ] **Step 1: Add `Manufacturer` to `DocumentRecord`**

In `DocumentRecord.cs`, add after `public string? RunId { get; set; }`:

```csharp
/// <summary>
/// Canonical manufacturer name, denormalized from the scraper that produced this record.
/// Stored in Cosmos for filtering; set by ScraperOrchestrator at upsert time.
/// </summary>
public string? Manufacturer { get; set; }
```

- [ ] **Step 2: Add `manufacturer` to `RawDocumentCosmosRecord`**

In `RawDocumentCosmosRecord.cs`, find the block of top-level `[JsonPropertyName]` properties (near `run_id`, `link_status`, etc.) and add:

```csharp
[JsonPropertyName("manufacturer")]
public string? Manufacturer { get; set; }
```

- [ ] **Step 3: Map `Manufacturer` in `CosmosRawDocumentRepository.UpsertRawAsync`**

In `CosmosRawDocumentRepository.cs`, find the method `UpsertRawAsync`. Inside it, locate where a new `RawDocumentCosmosRecord` is constructed (on first insert). Add `Manufacturer = record.Manufacturer` to that constructor or object initializer. It should look like:

```csharp
// Inside the "create new record" branch of UpsertRawAsync:
Manufacturer = record.Manufacturer,
```

Also check if there's an update branch that merges fields — `Manufacturer` should NOT be overwritten on re-discovery (it's immutable once set). Only set it in the insert/create path.

- [ ] **Step 4: Wire manufacturer in `ScraperOrchestrator`**

In `ScraperOrchestrator.cs`, run `grep -n "UpsertRawAsync\|BuildDocument\|DocumentRecord\|record\s*=" src/PinballWizard.Application/ScraperOrchestrator.cs` to find exactly where `record` is constructed. The `record` is a `DocumentRecord`; the orchestrator also has the current `ISourceScraper` in scope. Add:

```csharp
record.Manufacturer = scraper.Manufacturer;
```

immediately before the `await _rawDocRepo.UpsertRawAsync(record, cancellationToken)` call at line ~102.

- [ ] **Step 5: Build and run existing infrastructure tests**

```
dotnet build PinballWizard.slnx
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E&Project!=PinballWizard.Web.Tests" -v minimal
```

Expected: all pass. No compilation errors.

- [ ] **Step 6: Commit**

```
git add src/PinballWizard.Core/Models/DocumentRecord.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentCosmosRecord.cs \
        src/PinballWizard.Application/ScraperOrchestrator.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs
git commit -m "feat(catalog) denormalize manufacturer onto DocumentRecord + RawDocumentCosmosRecord"
```

---

### Task 3: Define `DocumentListItem` and `DocumentDetailRecord` DTOs

**Files:**
- Create: `src/PinballWizard.Application/Documents/DocumentListItem.cs`
- Create: `src/PinballWizard.Application/Documents/DocumentDetailRecord.cs`

**Interfaces:**
- Produces: `DocumentListItem`, `DocumentDetailRecord` — consumed by Tasks 4, 6, 7

- [ ] **Step 1: Create the `Documents` folder and `DocumentListItem`**

Create `src/PinballWizard.Application/Documents/DocumentListItem.cs`:

```csharp
namespace PinballWizard.Application.Documents;

/// <summary>
/// Projected row for the /documents list page. Admin-only fields are null
/// when <c>includeAdminFields</c> is false on the repository query.
/// </summary>
public sealed record DocumentListItem(
    string DocumentId,
    string Title,
    string DocumentType,
    string? GameTitle,
    string? Edition,
    string Manufacturer,
    string FileFormat,
    int? PageCount,
    long? SizeBytes,
    DateTimeOffset FirstDiscoveredAt,
    // Admin-only — null on public projection:
    string? LinkStatus,
    string? LinkFailureReason,
    string? ResolutionStrategy
);
```

- [ ] **Step 2: Create `DocumentDetailRecord`**

Create `src/PinballWizard.Application/Documents/DocumentDetailRecord.cs`:

```csharp
namespace PinballWizard.Application.Documents;

/// <summary>
/// Full provenance record for the /documents/{id} detail page.
/// Admin-only fields are null when <c>includeAdminFields</c> is false.
/// </summary>
public sealed record DocumentDetailRecord(
    string DocumentId,
    string Title,
    string DocumentType,
    string FileFormat,
    int? PageCount,
    long? SizeBytes,
    string FileUrl,
    string DiscoveryUrl,
    string? DiscoveryContext,
    string? SourceTab,
    string SourceType,
    string? GameTitle,
    string? GameSlug,
    string? Edition,
    string? EditionScope,
    string Manufacturer,
    DateTimeOffset FirstDiscoveredAt,
    DateTimeOffset? LastDownloadedAt,
    // Admin-only — null on public projection:
    string? LinkStatus,
    string? LinkFailureReason,
    string? ResolutionStrategy,
    IReadOnlyList<string>? LinkedMachineIds
);
```

- [ ] **Step 3: Build to verify the new types compile**

```
dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj
```

Expected: no errors.

- [ ] **Step 4: Commit**

```
git add src/PinballWizard.Application/Documents/
git commit -m "feat(documents) add DocumentListItem + DocumentDetailRecord DTOs"
```

---

### Task 4: Add `StreamDocumentsAsync` + `GetDocumentDetailAsync` to repository interface + implementation

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs`

**Interfaces:**
- Consumes: `DocumentListItem`, `DocumentDetailRecord` from Task 3; `RawDocumentCosmosRecord.Manufacturer` from Task 2
- Produces: `IRawDocumentRepository.StreamDocumentsAsync`, `IRawDocumentRepository.GetDocumentDetailAsync` — consumed by Tasks 6, 7

- [ ] **Step 1: Add the two signatures to `IRawDocumentRepository`**

In `IRawDocumentRepository.cs`, add after the existing methods:

```csharp
// Stream documents for the /documents browse page.
// Optionally filtered by game title (CONTAINS, case-insensitive) and/or manufacturer (exact match).
// Admin fields (link_status, failure_reason, resolution_strategy) are null when includeAdminFields=false.
IAsyncEnumerable<DocumentListItem> StreamDocumentsAsync(
    string? game,
    string? manufacturer,
    bool includeAdminFields,
    CancellationToken cancellationToken);

// Point read for the /documents/{id} detail page.
// Returns null if the document_id does not exist in the container.
Task<DocumentDetailRecord?> GetDocumentDetailAsync(
    string documentId,
    bool includeAdminFields,
    CancellationToken cancellationToken);
```

Add the `using PinballWizard.Application.Documents;` import at the top of the file.

- [ ] **Step 2: Implement `StreamDocumentsAsync` in `CosmosRawDocumentRepository`**

Add the following private helper and public method to `CosmosRawDocumentRepository.cs`:

```csharp
public async IAsyncEnumerable<DocumentListItem> StreamDocumentsAsync(
    string? game,
    string? manufacturer,
    bool includeAdminFields,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var query = new QueryDefinition(@"
        SELECT *
        FROM c
        WHERE (@game = '' OR (IS_DEFINED(c.game) AND IS_DEFINED(c.game.title)
               AND CONTAINS(LOWER(c.game.title), LOWER(@game))))
          AND (@manufacturer = '' OR c.manufacturer = @manufacturer)
        ORDER BY c.timeline.first_discovered_at DESC")
        .WithParameter("@game", game ?? "")
        .WithParameter("@manufacturer", manufacturer ?? "");

    await foreach (var raw in StreamCrossPartitionAsync<RawDocumentCosmosRecord>(query, cancellationToken))
    {
        yield return MapToListItem(raw, includeAdminFields);
    }
}

private static DocumentListItem MapToListItem(RawDocumentCosmosRecord r, bool includeAdminFields)
{
    var title = r.Source?.LinkText
        ?? System.IO.Path.GetFileName(r.Source?.FileUrl ?? "")
            .Split('?')[0]    // strip query string from filename
        ?? r.PartitionKey;

    return new DocumentListItem(
        DocumentId: r.PartitionKey,
        Title: title,
        DocumentType: r.Classification?.DocumentType ?? "",
        GameTitle: r.Game?.Title,
        Edition: r.Game?.Edition,
        Manufacturer: r.Manufacturer ?? "",
        FileFormat: r.Classification?.FileFormat ?? "",
        PageCount: r.File?.PageCount,
        SizeBytes: r.File?.SizeBytes,
        FirstDiscoveredAt: r.Timeline?.FirstDiscoveredAt ?? DateTimeOffset.MinValue,
        LinkStatus: includeAdminFields ? r.LinkStatus : null,
        LinkFailureReason: includeAdminFields ? r.LinkFailureReason : null,
        ResolutionStrategy: includeAdminFields ? r.ResolutionStrategy : null
    );
}
```

**Note on nested types:** `RawDocumentCosmosRecord` has nested record types (`RawSourceInfo`, `RawClassificationInfo`, `RawFileInfo`, `RawTimelineInfo`). Check the actual property names in `RawDocumentCosmosRecord.cs` before finalizing (e.g., `r.Source?.LinkText` — confirm `RawSourceInfo` has a `LinkText` C# property). The JSON wire names are `link_text`, `file_url`, etc., but the C# property names may differ.

- [ ] **Step 3: Implement `GetDocumentDetailAsync` in `CosmosRawDocumentRepository`**

Add below `StreamDocumentsAsync`:

```csharp
public async Task<DocumentDetailRecord?> GetDocumentDetailAsync(
    string documentId,
    bool includeAdminFields,
    CancellationToken cancellationToken)
{
    // Point read: partition key = document_id = id
    var raw = await GetByIdAsync(documentId, documentId, cancellationToken);
    if (raw is null) return null;

    var title = raw.Source?.LinkText
        ?? System.IO.Path.GetFileName(raw.Source?.FileUrl ?? "")
            .Split('?')[0]
        ?? raw.PartitionKey;

    return new DocumentDetailRecord(
        DocumentId: raw.PartitionKey,
        Title: title,
        DocumentType: raw.Classification?.DocumentType ?? "",
        FileFormat: raw.Classification?.FileFormat ?? "",
        PageCount: raw.File?.PageCount,
        SizeBytes: raw.File?.SizeBytes,
        FileUrl: raw.Source?.FileUrl ?? "",
        DiscoveryUrl: raw.Source?.DiscoveryUrl ?? "",
        DiscoveryContext: raw.Source?.DiscoveryContext,
        SourceTab: raw.Source?.Tab,
        SourceType: raw.Source?.SourceType ?? "",
        GameTitle: raw.Game?.Title,
        GameSlug: raw.Game?.Slug,
        Edition: raw.Game?.Edition,
        EditionScope: raw.Game?.EditionScope,
        Manufacturer: raw.Manufacturer ?? "",
        FirstDiscoveredAt: raw.Timeline?.FirstDiscoveredAt ?? DateTimeOffset.MinValue,
        LastDownloadedAt: raw.Timeline?.LastDownloadedAt,
        LinkStatus: includeAdminFields ? raw.LinkStatus : null,
        LinkFailureReason: includeAdminFields ? raw.LinkFailureReason : null,
        ResolutionStrategy: includeAdminFields ? raw.ResolutionStrategy : null,
        LinkedMachineIds: includeAdminFields ? raw.LinkedMachineIds?.AsReadOnly() : null
    );
}
```

**Note:** `GetByIdAsync` is the base class method from `CosmosRepository<T>`. Confirm the exact signature by reading the base class; it takes `(string id, string partitionKey, CancellationToken ct)`.

**Note:** `raw.Game?.EditionScope` — check whether `RawDocumentCosmosRecord` has an `EditionScope` field on the game nested type. If not, leave `EditionScope: null` for now.

Add the `using PinballWizard.Application.Documents;` import at the top of `CosmosRawDocumentRepository.cs`.

- [ ] **Step 4: Build**

```
dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj
```

Expected: no errors.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs
git commit -m "feat(documents) StreamDocumentsAsync + GetDocumentDetailAsync on IRawDocumentRepository"
```

---

### Task 5: Update `CrossPartitionQueryAllowListTests`

**Files:**
- Modify: `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs`

**Interfaces:**
- Consumes: `CosmosRawDocumentRepository.StreamDocumentsAsync` from Task 4

- [ ] **Step 1: Read the allow-list test to understand the exact format**

Open `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs` and look for the `allowList` collection. It contains file name strings like `"CosmosRawDocumentRepository.cs"`. `CosmosRawDocumentRepository.cs` is already in the list (it has existing cross-partition methods). Confirm it's there. If it is, no change is needed to the allow-list itself — the test already covers this file.

Run the test to confirm:

```
dotnet test PinballWizard.slnx --filter "FullyQualifiedName~CrossPartitionQueryAllowList" -v normal
```

Expected: PASS. If it fails (e.g., because `StreamDocumentsAsync` added a new call pattern), add `"CosmosRawDocumentRepository.cs"` to the allow-list (it's likely already there from existing methods).

- [ ] **Step 2: Commit (only if the file changed)**

```
git add tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs
git commit -m "test(arch) confirm CosmosRawDocumentRepository on cross-partition allow-list"
```

---

### Task 6: Build `DocumentList.razor` shared component + four list pages

**Files:**
- Create: `src/PinballWizard.Web/Components/Shared/DocumentList.razor`
- Create: `src/PinballWizard.Web/Components/Pages/Documents.razor`
- Create: `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor` *(placeholder, full impl in Task 7)*
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocuments.razor`
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor` *(placeholder, full impl in Task 7)*

**Interfaces:**
- Consumes: `IRawDocumentRepository.StreamDocumentsAsync` from Task 4; `DocumentListItem` from Task 3; `AppDataGrid`, `AppPageHeader`, `AppEmptyState`, `AppErrorAlert`, `AppStatusChip`, `AdminLoadingBar` from `Components/Shared/`
- Produces: `/documents` and `/admin/documents` routes; `IsAdmin` param — consumed by Task 9

- [ ] **Step 1: Create `DocumentList.razor` shared component**

Create `src/PinballWizard.Web/Components/Shared/DocumentList.razor`:

```razor
@namespace PinballWizard.Web.Components.Shared
@using PinballWizard.Application.Documents
@using PinballWizard.Application.Persistence
@inject IRawDocumentRepository Repo
@inject NavigationManager Nav

@if (_loadError)
{
    <AppErrorAlert data-testid="doc-list-load-error">
        Couldn't load documents — try refreshing.
    </AppErrorAlert>
}
else
{
    <MudStack Row="true" AlignItems="AlignItems.Center" Class="mb-4" Spacing="2">
        <MudTextField T="string"
                      Value="@Game"
                      ValueChanged="@(v => OnGameChanged(v))"
                      Placeholder="Search by game…"
                      Adornment="Adornment.Start"
                      AdornmentIcon="@Icons.Material.Filled.Search"
                      Clearable="true"
                      DebounceInterval="300"
                      Immediate="true"
                      data-testid="doc-list-game-filter" />
        <MudChipSet T="string"
                    SelectedValue="@Manufacturer"
                    SelectedValueChanged="@(v => OnManufacturerChanged(v))"
                    SelectionMode="SelectionMode.SingleSelection"
                    data-testid="doc-list-mfr-filter">
            @foreach (var mfr in _manufacturers)
            {
                <MudChip T="string" Value="@mfr" Variant="Variant.Outlined">@mfr</MudChip>
            }
        </MudChipSet>
    </MudStack>

    @if (_loading)
    {
        <AdminLoadingBar Label="Loading documents" />
    }

    <AppDataGrid T="DocumentListItem"
                 Items="@_documents"
                 data-testid="doc-list-grid"
                 RowClick="@OnRowClick">
        <Columns>
            <TemplateColumn Title="Title" Sortable="true">
                <CellTemplate>
                    <MudLink Href="@DocUrl(context.Item.DocumentId)">
                        @context.Item.Title
                    </MudLink>
                </CellTemplate>
            </TemplateColumn>
            <TemplateColumn Title="Type">
                <CellTemplate>
                    <AppStatusChip Color="Color.Default">@context.Item.DocumentType</AppStatusChip>
                </CellTemplate>
            </TemplateColumn>
            <TemplateColumn Title="Game">
                <CellTemplate>
                    @context.Item.GameTitle@(context.Item.Edition is not null ? $" {context.Item.Edition}" : "")
                </CellTemplate>
            </TemplateColumn>
            <PropertyColumn Property="x => x.Manufacturer" Title="Manufacturer" />
            <PropertyColumn Property="x => x.FileFormat" Title="Format" />
            <PropertyColumn Property="x => x.PageCount" Title="Pages" />
            <TemplateColumn Title="Discovered">
                <CellTemplate>
                    @context.Item.FirstDiscoveredAt.ToString("yyyy-MM-dd")
                </CellTemplate>
            </TemplateColumn>
            @if (IsAdmin)
            {
                <TemplateColumn Title="Link Status">
                    <CellTemplate>
                        @if (context.Item.LinkStatus is not null)
                        {
                            <AppStatusChip Color="@LinkStatusColor(context.Item.LinkStatus)">
                                @context.Item.LinkStatus
                            </AppStatusChip>
                        }
                    </CellTemplate>
                </TemplateColumn>
                <TemplateColumn Title="Failure Reason">
                    <CellTemplate>
                        <MudText Typo="Typo.caption" Style="max-width:200px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">
                            @context.Item.LinkFailureReason
                        </MudText>
                    </CellTemplate>
                </TemplateColumn>
            }
        </Columns>
        <NoRecordsContent>
            @if (Game is not null || Manufacturer is not null)
            {
                <AppEmptyState Title="No documents match"
                               Subtitle="Try a different game or manufacturer"
                               data-testid="doc-list-empty-filtered" />
            }
            else
            {
                <AppEmptyState Title="No documents indexed yet"
                               data-testid="doc-list-empty-corpus" />
            }
        </NoRecordsContent>
    </AppDataGrid>
}

@code {
    [Parameter] public bool IsAdmin { get; set; }
    [Parameter] public string? Game { get; set; }
    [Parameter] public string? Manufacturer { get; set; }

    private List<DocumentListItem> _documents = [];
    private bool _loading = true;
    private bool _loadError;

    private static readonly string[] _manufacturers =
    [
        "American Pinball", "Barrels of Fun", "Chicago Gaming",
        "Jersey Jack", "Multimorphic", "Pinball Brothers",
        "Spooky", "Stern"
    ];

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _loadError = false;
        _documents.Clear();

        try
        {
            await foreach (var item in Repo.StreamDocumentsAsync(Game, Manufacturer, IsAdmin, CancellationToken.None))
            {
                _documents.Add(item);
            }
        }
        catch (Exception)
        {
            _loadError = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnGameChanged(string? value)
    {
        var uri = Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["game"] = string.IsNullOrWhiteSpace(value) ? null : value,
            ["manufacturer"] = Manufacturer
        });
        Nav.NavigateTo(uri, replace: true);
    }

    private void OnManufacturerChanged(string? value)
    {
        var uri = Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["game"] = Game,
            ["manufacturer"] = string.IsNullOrWhiteSpace(value) ? null : value
        });
        Nav.NavigateTo(uri, replace: true);
    }

    private void OnRowClick(DataGridRowClickEventArgs<DocumentListItem> args) =>
        Nav.NavigateTo(DocUrl(args.Item.DocumentId));

    private string DocUrl(string documentId) =>
        IsAdmin ? $"/admin/documents/{documentId}" : $"/documents/{documentId}";

    private static Color LinkStatusColor(string? status) => status switch
    {
        "linked" or "manually_linked" => Color.Success,
        "failed" or "not_in_catalog" => Color.Error,
        "platform_generic" => Color.Warning,
        _ => Color.Default
    };
}
```

- [ ] **Step 2: Create the public list page**

Create `src/PinballWizard.Web/Components/Pages/Documents.razor`:

```razor
@page "/documents"
@attribute [AllowAnonymous]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Web.Components.Shared
@rendermode InteractiveServer

<PageTitle>PinballWizard — Documents</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="mt-6">
    <AppPageHeader Title="Documents"
                   Subtitle="Browse every manual, schematic, firmware, and ruleset we've indexed — with full source provenance." />
    <DocumentList IsAdmin="false" Game="@Game" Manufacturer="@Manufacturer" />
</MudContainer>

@code {
    [SupplyParameterFromQuery(Name = "game")] public string? Game { get; set; }
    [SupplyParameterFromQuery(Name = "manufacturer")] public string? Manufacturer { get; set; }
}
```

- [ ] **Step 3: Create the admin list page**

Create `src/PinballWizard.Web/Components/Pages/Admin/AdminDocuments.razor`:

```razor
@page "/admin/documents"
@layout AdminLayout
@attribute [Authorize(Policy = "AdminOnly")]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Web.Components.Layout
@using PinballWizard.Web.Components.Shared
@rendermode InteractiveServer

<PageTitle>PinballWizard Admin — Documents</PageTitle>

<AppPageHeader Title="Documents"
               Subtitle="Full corpus with triage status and failure reasons." />
<DocumentList IsAdmin="true" Game="@Game" Manufacturer="@Manufacturer" />

@code {
    [SupplyParameterFromQuery(Name = "game")] public string? Game { get; set; }
    [SupplyParameterFromQuery(Name = "manufacturer")] public string? Manufacturer { get; set; }
}
```

- [ ] **Step 4: Create placeholder detail pages** (full implementation in Task 7)

Create `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor`:

```razor
@page "/documents/{DocumentId}"
@attribute [AllowAnonymous]
@using Microsoft.AspNetCore.Authorization
@rendermode InteractiveServer

<PageTitle>PinballWizard — Document</PageTitle>
<MudText>Detail coming in Task 7.</MudText>

@code {
    [Parameter] public string DocumentId { get; set; } = null!;
}
```

Create `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor`:

```razor
@page "/admin/documents/{DocumentId}"
@layout AdminLayout
@attribute [Authorize(Policy = "AdminOnly")]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Web.Components.Layout
@rendermode InteractiveServer

<PageTitle>PinballWizard Admin — Document</PageTitle>
<MudText>Detail coming in Task 7.</MudText>

@code {
    [Parameter] public string DocumentId { get; set; } = null!;
}
```

- [ ] **Step 5: Build the web project**

```
dotnet build src/PinballWizard.Web/PinballWizard.Web.csproj
```

Expected: no errors.

- [ ] **Step 6: Commit**

```
git add src/PinballWizard.Web/Components/Shared/DocumentList.razor \
        src/PinballWizard.Web/Components/Pages/Documents.razor \
        src/PinballWizard.Web/Components/Pages/DocumentDetail.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminDocuments.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor
git commit -m "feat(documents) DocumentList shared component + public/admin list pages"
```

---

### Task 7: Build `DocumentDetail.razor` shared component + replace placeholder pages

**Files:**
- Create: `src/PinballWizard.Web/Components/Shared/DocumentDetail.razor`
- Modify: `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor`

**Interfaces:**
- Consumes: `IRawDocumentRepository.GetDocumentDetailAsync` from Task 4; `DocumentDetailRecord` from Task 3
- Produces: `/documents/{id}` and `/admin/documents/{id}` routes — consumed by Task 9

- [ ] **Step 1: Create `DocumentDetail.razor` shared component**

Create `src/PinballWizard.Web/Components/Shared/DocumentDetail.razor`:

```razor
@namespace PinballWizard.Web.Components.Shared
@using PinballWizard.Application.Documents
@using PinballWizard.Application.Persistence
@inject IRawDocumentRepository Repo
@inject NavigationManager Nav

@if (_loading)
{
    <AdminLoadingBar Label="Loading document" />
}
else if (_loadError)
{
    <AppErrorAlert data-testid="doc-detail-load-error">
        Couldn't load document — try refreshing.
    </AppErrorAlert>
    <MudLink Href="@BackUrl" Class="mt-2">← All documents</MudLink>
}
else if (_doc is null)
{
    <AppErrorAlert data-testid="doc-detail-not-found">
        Document not found.
    </AppErrorAlert>
    <MudLink Href="@BackUrl" Class="mt-2">← All documents</MudLink>
}
else
{
    <MudLink Href="@BackUrl" Class="mb-4 d-block" data-testid="doc-detail-back-link">← All documents</MudLink>

    <MudGrid>
        <MudItem xs="12" md="8">
            <MudCard Elevation="2" data-testid="doc-detail-card">
                <MudCardContent>
                    <MudText Typo="Typo.h5" Class="mb-3" data-testid="doc-detail-title">@_doc.Title</MudText>

                    <MudStack Row="true" Spacing="1" Class="mb-3">
                        <AppStatusChip Color="Color.Primary">@_doc.DocumentType</AppStatusChip>
                        <AppStatusChip Color="Color.Default">@_doc.FileFormat.ToUpperInvariant()</AppStatusChip>
                    </MudStack>

                    @if (_doc.GameTitle is not null)
                    {
                        <MudText Typo="Typo.body1" Class="mb-1" data-testid="doc-detail-game">
                            @_doc.GameTitle@(_doc.Edition is not null ? $" {_doc.Edition}" : "")
                        </MudText>
                    }

                    @if (_doc.EditionScope is not null)
                    {
                        <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-1">
                            @EditionScopeLabel(_doc.EditionScope)
                        </MudText>
                    }

                    <MudText Typo="Typo.body2" Class="mb-3" data-testid="doc-detail-manufacturer">
                        @_doc.Manufacturer
                    </MudText>

                    <MudDivider Class="my-3" />

                    <MudText Typo="Typo.caption" Color="Color.Secondary">Found on</MudText>
                    <MudLink Href="@_doc.DiscoveryUrl" Target="_blank" Typo="Typo.body2"
                             data-testid="doc-detail-discovery-url">
                        @_doc.DiscoveryUrl
                    </MudLink>

                    @if (_doc.DiscoveryContext is not null)
                    {
                        <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mt-1"
                                 data-testid="doc-detail-discovery-context">
                            @_doc.DiscoveryContext
                        </MudText>
                    }

                    <MudDivider Class="my-3" />

                    <MudStack Row="true" Spacing="4">
                        <MudStack Spacing="0">
                            <MudText Typo="Typo.caption" Color="Color.Secondary">Discovered</MudText>
                            <MudText Typo="Typo.body2">@_doc.FirstDiscoveredAt.ToString("yyyy-MM-dd")</MudText>
                        </MudStack>
                        @if (_doc.LastDownloadedAt.HasValue)
                        {
                            <MudStack Spacing="0">
                                <MudText Typo="Typo.caption" Color="Color.Secondary">Last downloaded</MudText>
                                <MudText Typo="Typo.body2">@_doc.LastDownloadedAt.Value.ToString("yyyy-MM-dd")</MudText>
                            </MudStack>
                        }
                        @if (_doc.SizeBytes.HasValue)
                        {
                            <MudStack Spacing="0">
                                <MudText Typo="Typo.caption" Color="Color.Secondary">Size</MudText>
                                <MudText Typo="Typo.body2">@FormatSize(_doc.SizeBytes.Value)</MudText>
                            </MudStack>
                        }
                        @if (_doc.PageCount.HasValue)
                        {
                            <MudStack Spacing="0">
                                <MudText Typo="Typo.caption" Color="Color.Secondary">Pages</MudText>
                                <MudText Typo="Typo.body2">@_doc.PageCount</MudText>
                            </MudStack>
                        }
                    </MudStack>
                </MudCardContent>
                <MudCardActions>
                    <MudButton Variant="Variant.Filled"
                               Color="Color.Primary"
                               Href="@_doc.FileUrl"
                               Target="_blank"
                               data-testid="doc-detail-open-btn">
                        Open document →
                    </MudButton>
                </MudCardActions>
            </MudCard>
        </MudItem>

        @if (IsAdmin)
        {
            <MudItem xs="12" md="4">
                <MudCard Elevation="2" data-testid="doc-detail-admin-panel">
                    <MudCardContent>
                        <MudText Typo="Typo.subtitle2" Class="mb-3">Admin Details</MudText>

                        @if (_doc.LinkStatus is not null)
                        {
                            <MudText Typo="Typo.caption" Color="Color.Secondary">Link Status</MudText>
                            <AppStatusChip Color="@LinkStatusColor(_doc.LinkStatus)" Class="mb-2">
                                @_doc.LinkStatus
                            </AppStatusChip>
                        }

                        @if (_doc.ResolutionStrategy is not null)
                        {
                            <MudText Typo="Typo.caption" Color="Color.Secondary">Resolution</MudText>
                            <MudText Typo="Typo.body2" Class="mb-2">@_doc.ResolutionStrategy</MudText>
                        }

                        @if (_doc.LinkFailureReason is not null)
                        {
                            <AppErrorAlert Class="mb-2">@_doc.LinkFailureReason</AppErrorAlert>
                        }

                        <MudText Typo="Typo.caption" Color="Color.Secondary">Document ID</MudText>
                        <MudText Typo="Typo.caption" Class="mb-2" data-testid="doc-detail-doc-id">
                            @_doc.DocumentId
                        </MudText>

                        @if (_doc.LinkedMachineIds?.Count > 0)
                        {
                            <MudText Typo="Typo.caption" Color="Color.Secondary" Class="mb-1">Linked Machines</MudText>
                            <MudStack Row="true" Wrap="Wrap.Wrap">
                                @foreach (var id in _doc.LinkedMachineIds)
                                {
                                    <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined">@id</MudChip>
                                }
                            </MudStack>
                        }
                    </MudCardContent>
                </MudCard>
            </MudItem>
        }
    </MudGrid>
}

@code {
    [Parameter, EditorRequired] public string DocumentId { get; set; } = null!;
    [Parameter] public bool IsAdmin { get; set; }

    private DocumentDetailRecord? _doc;
    private bool _loading = true;
    private bool _loadError;

    private string BackUrl => IsAdmin ? "/admin/documents" : "/documents";

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _loadError = false;
        _doc = null;

        try
        {
            _doc = await Repo.GetDocumentDetailAsync(DocumentId, IsAdmin, CancellationToken.None);
        }
        catch (Exception)
        {
            _loadError = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string EditionScopeLabel(string scope) => scope switch
    {
        "franchise-wide" => "Franchise-wide",
        "edition-subset" => "Edition subset",
        "single-edition" => "Single edition",
        _ => scope
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024 * 1024):F1} MB"
    };

    private static Color LinkStatusColor(string? status) => status switch
    {
        "linked" or "manually_linked" => Color.Success,
        "failed" or "not_in_catalog" => Color.Error,
        "platform_generic" => Color.Warning,
        _ => Color.Default
    };
}
```

- [ ] **Step 2: Replace public detail page placeholder**

Replace the contents of `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor`:

```razor
@page "/documents/{DocumentId}"
@attribute [AllowAnonymous]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Web.Components.Shared
@rendermode InteractiveServer

<PageTitle>PinballWizard — Document</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="mt-6">
    <DocumentDetail DocumentId="@DocumentId" IsAdmin="false" />
</MudContainer>

@code {
    [Parameter] public string DocumentId { get; set; } = null!;
}
```

- [ ] **Step 3: Replace admin detail page placeholder**

Replace the contents of `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor`:

```razor
@page "/admin/documents/{DocumentId}"
@layout AdminLayout
@attribute [Authorize(Policy = "AdminOnly")]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Web.Components.Layout
@using PinballWizard.Web.Components.Shared
@rendermode InteractiveServer

<PageTitle>PinballWizard Admin — Document</PageTitle>

<DocumentDetail DocumentId="@DocumentId" IsAdmin="true" />

@code {
    [Parameter] public string DocumentId { get; set; } = null!;
}
```

- [ ] **Step 4: Build**

```
dotnet build src/PinballWizard.Web/PinballWizard.Web.csproj
```

Expected: no errors.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Web/Components/Shared/DocumentDetail.razor \
        src/PinballWizard.Web/Components/Pages/DocumentDetail.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentDetail.razor
git commit -m "feat(documents) DocumentDetail shared component + public/admin detail pages"
```

---

### Task 8: Navigation integration

**Files:**
- Modify: `src/PinballWizard.Web/Components/Theming/BrandHeader.razor`
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor`

**Interfaces:**
- Consumes: `/documents` and `/documents/{id}` routes from Tasks 6, 7

- [ ] **Step 1: Add Documents to `BrandHeader.razor`**

In `BrandHeader.razor`, find the `<nav>` block containing the two `MudButton` elements (`/about` and `/admin`). Add a new `MudButton` for Documents between them:

```razor
<MudButton Href="/documents" Color="Color.Inherit" Variant="Variant.Text">Documents</MudButton>
```

The full nav block should read:
```razor
<MudButton Href="/about" ...>What we cover</MudButton>
<MudButton Href="/documents" Color="Color.Inherit" Variant="Variant.Text">Documents</MudButton>
<MudButton Href="/admin" ...>Behind the Scenes</MudButton>
```

Match the `Color` and `Variant` values of the existing buttons exactly.

- [ ] **Step 2: Add Documents to `AdminLayout.razor` drawer**

In `AdminLayout.razor`, find the `MudNavMenu` block. After the Machines `MudNavLink`, add:

```razor
<MudNavLink Href="/admin/documents" Match="NavLinkMatch.Prefix"
            Icon="@Icons.Material.Filled.Article">
    Documents
</MudNavLink>
```

Use the same icon as makes sense — `Icons.Material.Filled.Article` or `Icons.Material.Filled.Description`. Match the `Match` and `Href` pattern of the existing nav links.

- [ ] **Step 3: Link document titles in `AdminMachineDetail.razor`**

In `AdminMachineDetail.razor`, find the "Document" column `TemplateColumn` in the linked-documents `AppDataGrid`. Currently the cell template renders a `MudLink` to `d.DocumentUrl` (the raw file URL). Replace it so the title links to the documents detail page instead:

Find this pattern (approximately):
```razor
<MudLink Href="@context.Item.DocumentUrl" Target="_blank">
    @(context.Item.LinkText ?? FileNameFromUrl(context.Item.DocumentUrl))
</MudLink>
```

Replace with:
```razor
<MudStack Spacing="0">
    <MudLink Href="@($"/documents/{context.Item.DocumentId}")"
             data-testid="detail-doc-link">
        @(context.Item.LinkText ?? FileNameFromUrl(context.Item.DocumentUrl))
    </MudLink>
    <MudLink Href="@context.Item.DocumentUrl" Target="_blank" Typo="Typo.caption"
             Color="Color.Secondary">
        Open file ↗
    </MudLink>
</MudStack>
```

This gives users both the provenance detail link AND the direct file link.

- [ ] **Step 4: Build and run full test suite**

```
dotnet build PinballWizard.slnx
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E" -v minimal
```

Expected: all pass.

- [ ] **Step 5: Commit**

```
git add src/PinballWizard.Web/Components/Theming/BrandHeader.razor \
        src/PinballWizard.Web/Components/Layout/AdminLayout.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor
git commit -m "feat(documents) nav integration — BrandHeader, AdminLayout drawer, AdminMachineDetail doc links"
```

---

### Task 9: bUnit tests

**Files:**
- Create: `tests/PinballWizard.Web.Tests/Components/DocumentListTests.cs`
- Create: `tests/PinballWizard.Web.Tests/Components/DocumentDetailTests.cs`

**Interfaces:**
- Consumes: `DocumentList.razor`, `DocumentDetail.razor` from Tasks 6, 7; `IRawDocumentRepository.StreamDocumentsAsync`, `IRawDocumentRepository.GetDocumentDetailAsync` from Task 4

- [ ] **Step 1: Write `DocumentListTests.cs`**

Create `tests/PinballWizard.Web.Tests/Components/DocumentListTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components;

public class DocumentListTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();

    public DocumentListTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => _repo);
        this.AddAuthorization().SetAuthorized("test@example.com");
    }

    private static async IAsyncEnumerable<DocumentListItem> FakeStream(
        IEnumerable<DocumentListItem> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
            yield return item;
    }

    private static DocumentListItem MakeItem(string id = "doc_abc", string game = "Godzilla",
        string mfr = "Stern") =>
        new(id, $"{game} Manual", "Manual", game, "Pro", mfr,
            "pdf", 150, 5_200_000, DateTimeOffset.UtcNow,
            null, null, null);

    [Fact]
    public async Task ShowsDocumentsFromRepository()
    {
        var item = MakeItem();
        _repo.StreamDocumentsAsync(null, null, false, default)
             .Returns(_ => FakeStream([item]));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/documents");

        var cut = RenderComponent<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Godzilla Manual", cut.Markup);
    }

    [Fact]
    public async Task EmptyCorpus_ShowsEmptyState()
    {
        _repo.StreamDocumentsAsync(null, null, false, default)
             .Returns(_ => FakeStream([]));

        var cut = RenderComponent<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-empty-corpus']");
    }

    [Fact]
    public async Task WithFilters_NoResults_ShowsFilteredEmptyState()
    {
        _repo.StreamDocumentsAsync("Godzilla", null, false, default)
             .Returns(_ => FakeStream([]));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/documents?game=Godzilla");

        var cut = RenderComponent<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-empty-filtered']");
    }

    [Fact]
    public async Task GameQueryParam_InitializesGameFilter()
    {
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), false, default)
             .Returns(_ => FakeStream([]));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/documents?game=Godzilla");

        var cut = RenderComponent<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var input = cut.Find("[data-testid='doc-list-game-filter'] input");
        Assert.Equal("Godzilla", input.GetAttribute("value"));
    }

    [Fact]
    public async Task AdminColumns_HiddenOnPublicPage()
    {
        _repo.StreamDocumentsAsync(null, null, false, default)
             .Returns(_ => FakeStream([MakeItem()]));

        var cut = RenderComponent<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("Link Status", cut.Markup);
        Assert.DoesNotContain("Failure Reason", cut.Markup);
    }

    [Fact]
    public async Task AdminPage_ShowsAdminColumns()
    {
        var item = MakeItem() with { LinkStatus = "linked" };
        _repo.StreamDocumentsAsync(null, null, true, default)
             .Returns(_ => FakeStream([item]));

        var cut = RenderComponent<PinballWizard.Web.Components.Pages.Admin.AdminDocuments>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Link Status", cut.Markup);
    }

    [Fact]
    public async Task RepositoryError_ShowsErrorAlert()
    {
        _repo.StreamDocumentsAsync(null, null, false, default)
             .Returns(_ => throw new InvalidOperationException("Cosmos down"));

        var cut = RenderComponent<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-load-error']");
    }
}
```

- [ ] **Step 2: Write `DocumentDetailTests.cs`**

Create `tests/PinballWizard.Web.Tests/Components/DocumentDetailTests.cs`:

```csharp
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components;

public class DocumentDetailTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();
    private const string FakeDocId = "doc_abc123";

    public DocumentDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped(_ => _repo);
        this.AddAuthorization().SetAuthorized("test@example.com");
    }

    private static DocumentDetailRecord MakeDetail(string? linkStatus = null) =>
        new(FakeDocId, "Godzilla Pro Manual", "Manual", "pdf",
            PageCount: 150, SizeBytes: 5_200_000,
            FileUrl: "https://sternpinball.com/docs/godzilla-pro-manual.pdf",
            DiscoveryUrl: "https://sternpinball.com/game/godzilla/",
            DiscoveryContext: "Game Page → Specs & Manual tab",
            SourceTab: "Specs & Manual",
            SourceType: "GamePage",
            GameTitle: "Godzilla",
            GameSlug: "godzilla",
            Edition: "Pro",
            EditionScope: "single-edition",
            Manufacturer: "Stern",
            FirstDiscoveredAt: DateTimeOffset.UtcNow,
            LastDownloadedAt: DateTimeOffset.UtcNow,
            LinkStatus: linkStatus,
            LinkFailureReason: linkStatus is "failed" ? "No match found" : null,
            ResolutionStrategy: linkStatus is "linked" ? "title match" : null,
            LinkedMachineIds: linkStatus is "linked" ? ["G4do5-MkPnV"] : null);

    [Fact]
    public async Task RendersProvenanceCard()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, default)
             .Returns(MakeDetail());

        var cut = RenderComponent<DocumentDetail>(p => p.Add(x => x.DocumentId, FakeDocId));
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Godzilla Pro Manual", cut.Markup);
        Assert.Contains("Game Page → Specs &amp; Manual tab", cut.Markup);
        Assert.Contains("Stern", cut.Markup);
    }

    [Fact]
    public async Task OpenDocumentButton_HasCorrectHref()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, default)
             .Returns(MakeDetail());

        var cut = RenderComponent<DocumentDetail>(p => p.Add(x => x.DocumentId, FakeDocId));
        await cut.InvokeAsync(() => Task.CompletedTask);

        var btn = cut.Find("[data-testid='doc-detail-open-btn']");
        Assert.Equal("https://sternpinball.com/docs/godzilla-pro-manual.pdf",
            btn.GetAttribute("href"));
    }

    [Fact]
    public async Task NotFound_ShowsErrorAndBackLink()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, default)
             .Returns((DocumentDetailRecord?)null);

        var cut = RenderComponent<DocumentDetail>(p => p.Add(x => x.DocumentId, FakeDocId));
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-not-found']");
        cut.Find("[data-testid='doc-detail-back-link']");
    }

    [Fact]
    public async Task AdminPanel_HiddenOnPublicComponent()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, default)
             .Returns(MakeDetail("linked"));

        var cut = RenderComponent<DocumentDetail>(p =>
        {
            p.Add(x => x.DocumentId, FakeDocId);
            p.Add(x => x.IsAdmin, false);
        });
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("doc-detail-admin-panel", cut.Markup);
        Assert.DoesNotContain("doc-detail-doc-id", cut.Markup);
    }

    [Fact]
    public async Task AdminPanel_VisibleWhenIsAdminTrue()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, true, default)
             .Returns(MakeDetail("linked"));

        var cut = RenderComponent<DocumentDetail>(p =>
        {
            p.Add(x => x.DocumentId, FakeDocId);
            p.Add(x => x.IsAdmin, true);
        });
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-admin-panel']");
        cut.Find("[data-testid='doc-detail-doc-id']");
    }

    [Fact]
    public async Task RepositoryError_ShowsErrorAlert()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, default)
             .Returns(_ => throw new InvalidOperationException("Cosmos down"));

        var cut = RenderComponent<DocumentDetail>(p => p.Add(x => x.DocumentId, FakeDocId));
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-load-error']");
    }
}
```

- [ ] **Step 3: Run the new tests**

```
dotnet test PinballWizard.slnx --filter "FullyQualifiedName~DocumentListTests|FullyQualifiedName~DocumentDetailTests" -v normal
```

Expected: all pass. If a test fails due to a missing `data-testid` attribute, add it to the razor component and re-run.

- [ ] **Step 4: Run full suite to check for regressions**

```
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E" -v minimal
```

Expected: all pass.

- [ ] **Step 5: Commit**

```
git add tests/PinballWizard.Web.Tests/Components/DocumentListTests.cs \
        tests/PinballWizard.Web.Tests/Components/DocumentDetailTests.cs
git commit -m "test(documents) bUnit tests for DocumentList + DocumentDetail components"
```

---

## Self-Review

**Spec coverage check:**

| Spec requirement | Task |
|---|---|
| `/documents` public list page | Task 6 |
| `/documents/{id}` public detail page | Task 7 |
| `/admin/documents` admin list page | Task 6 |
| `/admin/documents/{id}` admin detail page | Task 7 |
| `manufacturer` denormalization in Cosmos | Tasks 1–2 |
| Game + manufacturer filter controls | Task 6 (DocumentList.razor) |
| URL query params (`?game=&manufacturer=`) | Task 6 (page components) |
| Filter changes update URL (replace state) | Task 6 (OnGameChanged/OnManufacturerChanged) |
| Admin-only columns (LinkStatus, FailureReason) | Task 6 (conditional columns) |
| Admin-only detail panel (doc ID, machines) | Task 7 (conditional MudItem) |
| `AppDataGrid` usage | Task 6 |
| `AppPageHeader` usage | Task 6 |
| `AppEmptyState` for empty corpus + filtered | Task 6 |
| `AppErrorAlert` for load error + not-found | Tasks 6, 7 |
| `AdminLoadingBar` while loading | Tasks 6, 7 |
| BrandHeader Documents button | Task 8 |
| AdminLayout nav link | Task 8 |
| AdminMachineDetail deep link | Task 8 |
| `CrossPartitionQueryAllowListTests` | Task 5 |
| bUnit tests: filter init, empty, admin columns, error | Task 9 |
| bUnit tests: provenance card, not-found, admin panel | Task 9 |

All spec requirements covered. ✓

**Placeholder scan:** No TBDs, no "implement later" phrases. All code blocks are complete. ✓

**Type consistency check:**
- `DocumentListItem` defined in Task 3, consumed in Tasks 4, 6, 9 — record parameter names consistent.
- `DocumentDetailRecord` defined in Task 3, consumed in Tasks 4, 7, 9 — record parameter names consistent.
- `IsAdmin` param: `bool`, used in Tasks 6, 7, 9 — consistent.
- `IRawDocumentRepository.StreamDocumentsAsync(string? game, string? manufacturer, bool includeAdminFields, CancellationToken)` — matches interface addition in Task 4 and usage in Task 6. ✓
- `IRawDocumentRepository.GetDocumentDetailAsync(string documentId, bool includeAdminFields, CancellationToken)` — matches interface addition in Task 4 and usage in Task 7. ✓
