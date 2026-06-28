# Documents Page — Design Spec

**Date:** 2026-06-27
**Branch:** feat/documents-page
**Status:** Approved — ready for implementation planning

---

## Overview

Add a public-facing **Documents** browse surface (`/documents`) and a public **Document Detail** page (`/documents/{documentId}`), each mirrored by an admin-only variant (`/admin/documents` and `/admin/documents/{documentId}`). The shared components from ADR-0046 are already built and available.

The primary goals are:

1. **Showcase catalog breadth** — let prospects browse the full indexed document corpus filtered by game and manufacturer.
2. **Showcase provenance depth** — each document has its own canonical URL that tells the full discovery chain (source page, tab, game, edition, when first found).
3. **Enable deep linking** — Wizard citations and machine detail pages can link directly to `/documents/{documentId}` or to a pre-filtered list via query params.

---

## Routes and Layouts

| Route | Layout | Render Mode | Auth |
|---|---|---|---|
| `/documents` | MainLayout | InteractiveServer | `[AllowAnonymous]` |
| `/documents/{DocumentId}` | MainLayout | InteractiveServer | `[AllowAnonymous]` |
| `/admin/documents` | AdminLayout | InteractiveServer | `[Authorize(Policy = "AdminOnly")]` |
| `/admin/documents/{DocumentId}` | AdminLayout | InteractiveServer | `[Authorize(Policy = "AdminOnly")]` |

Each public page and its admin counterpart use the **same underlying component**, parameterized by `IsAdmin`. The admin pages declare `@layout AdminLayout` and `[Authorize(Policy = "AdminOnly")]`; public pages are `[AllowAnonymous]`.

---

## Navigation Integration

- **`BrandHeader.razor`** — add a **Documents** `MudButton` alongside "What we cover", linking to `/documents`.
- **`AdminLayout.razor` drawer** — add a **Documents** `MudNavLink` to `/admin/documents` after Machines (Sources → Machines → Documents).
- **`AdminMachineDetail.razor`** — existing "Linked Documents" grid rows gain a link to `/documents/{documentId}` (the document title becomes a `MudLink`).
- **Wizard citations** — when the Wizard returns a citation with a `DocumentId`, the citation link targets `/documents/{documentId}` instead of (or in addition to) the raw file URL. (Tracked as a follow-up; out of scope for v1.)

---

## Data Model

### `manufacturer` denormalization

`RawDocumentCosmosRecord` does not currently carry a `manufacturer` field. Each scraper knows its manufacturer at write time. Add `manufacturer` (string, snake_case) to `RawDocumentCosmosRecord` and populate it in the ingestion pipeline. This aligns with `ScrapedDocumentRecord.manufacturer` which already has it.

Valid values (one per scraper): `"Stern"`, `"Jersey Jack"`, `"Spooky"`, `"American Pinball"`, `"Pinball Brothers"`, `"Barrels of Fun"`, `"Multimorphic"`, `"Chicago Gaming"`.

### Read model DTOs

**`DocumentListItem`** — returned by the list query:

```csharp
record DocumentListItem(
    string DocumentId,
    string Title,               // Source.LinkText ?? filename-from-URL ?? DocumentId
    string DocumentType,        // Classification.DocumentType
    string? GameTitle,          // Game.Title
    string? Edition,            // Game.Edition
    string Manufacturer,        // manufacturer (denormalized)
    string FileFormat,          // Classification.FileFormat
    int? PageCount,             // File.PageCount
    long? SizeBytes,            // File.SizeBytes
    DateTimeOffset FirstDiscoveredAt,
    // Admin-only (null on public projection):
    string? LinkStatus,
    string? LinkFailureReason,
    string? ResolutionStrategy
);
```

**`DocumentDetailRecord`** — returned by the point read:

```csharp
record DocumentDetailRecord(
    string DocumentId,
    string Title,
    string DocumentType,
    string FileFormat,
    int? PageCount,
    long? SizeBytes,
    string FileUrl,             // Source.FileUrl — primary CTA target
    string DiscoveryUrl,        // Source.DiscoveryUrl — "found on" link
    string? DiscoveryContext,   // Source.DiscoveryContext ("Game Page → Specs & Manual tab")
    string? SourceTab,          // Source.Tab
    string SourceType,          // Source.SourceType
    string? GameTitle,
    string? GameSlug,
    string? Edition,
    string? EditionScope,       // franchise-wide / edition-subset / single-edition
    string Manufacturer,
    DateTimeOffset FirstDiscoveredAt,
    DateTimeOffset? LastDownloadedAt,
    // Admin-only (null on public projection):
    string? LinkStatus,
    string? LinkFailureReason,
    string? ResolutionStrategy,
    IReadOnlyList<string>? LinkedMachineIds,
    string? RawDocumentId       // internal ID shown admin-only
);
```

---

## Data Layer

### Repository method

Add to `IRawDocumentRepository`:

```csharp
IAsyncEnumerable<DocumentListItem> StreamDocumentsAsync(
    string? game,
    string? manufacturer,
    bool includeAdminFields,
    CancellationToken ct);

Task<DocumentDetailRecord?> GetDocumentDetailAsync(
    string documentId,
    bool includeAdminFields,
    CancellationToken ct);
```

**`StreamDocumentsAsync`** executes a cross-partition Cosmos query:

```sql
SELECT
    c.document_id, c.source.link_text, c.source.file_url,
    c.classification.document_type, c.classification.file_format,
    c.game.title, c.game.edition,
    c.manufacturer,
    c.file.page_count, c.file.size_bytes,
    c.timeline.first_discovered_at,
    c.link_status, c.link_failure_reason, c.resolution_strategy
FROM c
WHERE
    (@game IS NULL OR CONTAINS(LOWER(c.game.title), LOWER(@game)))
    AND (@manufacturer IS NULL OR c.manufacturer = @manufacturer)
ORDER BY c.timeline.first_discovered_at DESC
```

`includeAdminFields = false` projects `null` for `link_status`, `link_failure_reason`, `resolution_strategy` to avoid sending operational data to the public projection.

**`GetDocumentDetailAsync`** is a point read by `document_id` (partition key = `document_id`) — cheap, single-partition.

### Cross-partition allow-list

Add `StreamDocumentsAsync` cross-partition query to `CrossPartitionQueryAllowListTests`.

---

## List Page — `DocumentList.razor`

**Component parameter:** `[Parameter] public bool IsAdmin { get; set; }`

**Query params (via `[SupplyParameterFromQuery]`):**
- `Game` — string?, initializes game filter text field
- `Manufacturer` — string?, initializes selected manufacturer chip

**Filter controls** (toolbar above the grid):
- `MudTextField` — "Search by game…" — two-way bound to `Game`, triggers re-query on change (debounced ~300ms), updates URL
- `MudChipSet` — one `MudChip` per manufacturer, single-select, clearable, bound to `Manufacturer`, triggers re-query + URL update

Filter changes call `NavigationManager.NavigateTo` with updated query params (replace state, not push) to keep the URL bookmarkable.

**Grid — `AppDataGrid<DocumentListItem>`:**

| Column | Public | Admin |
|---|---|---|
| Title (`MudLink` → detail page) | ✓ | ✓ |
| Type (`AppStatusChip`) | ✓ | ✓ |
| Game + Edition | ✓ | ✓ |
| Manufacturer | ✓ | ✓ |
| Format | ✓ | ✓ |
| Pages | ✓ | ✓ |
| Discovered (short date) | ✓ | ✓ |
| Link Status (`AppStatusChip`) | — | ✓ |
| Failure Reason (truncated text) | — | ✓ |

`NoRecordsContent` slot uses `AppEmptyState`:
- Active filters: "No documents match — try a different game or manufacturer"
- No filters, empty corpus: "No documents indexed yet"

Loading state: `AdminLoadingBar` shown while awaiting the first Cosmos result.

Row click navigates to `/documents/{documentId}` (public) or `/admin/documents/{documentId}` (admin).

---

## Detail Page — `DocumentDetail.razor`

**Component parameter:** `[Parameter] public bool IsAdmin { get; set; }`

**Route parameter:** `[Parameter] public string DocumentId { get; set; } = null!;`

**Layout — two columns on wide screens, stacked on mobile (`MudGrid`):**

Main column — provenance card (`MudCard Elevation=2`):
- **Title** (`MudText Typo.h5`)
- **Type** + **Format** chips (`AppStatusChip`) side by side
- **Game** — `GameTitle + " " + Edition` as `MudText`
- **Edition scope** — "Franchise-wide" / "Single edition" / "Edition subset"
- **Manufacturer** — plain text
- **Source page** — "Found on: [DiscoveryUrl]" as `MudLink` (opens new tab)
- **Discovery context** — "Game Page → Specs & Manual tab" as `MudText Color.Secondary`
- **Discovered** / **Last downloaded** — two date rows
- **File size** + **Page count** (if PDF)
- **Primary CTA** — `MudButton Variant.Filled` "Open document →" linking to `FileUrl` (new tab)
- **Back link** — `MudLink` "← All documents" → `/documents` or `/admin/documents`

Aside column — admin-only panel (hidden when `!IsAdmin`):
- **Link Status** chip
- **Resolution strategy** text
- **Failure reason** `AppErrorAlert` (only if status is Failed/NotInCatalog)
- **Document ID** (`MudText Typo.caption`)
- **Linked machine IDs** (list of `MudChip`)

**Not-found state:** `AppErrorAlert` "Document not found" + back link.
**Error state:** `AppErrorAlert` "Couldn't load document — try refreshing."

---

## Deep-Link Contract

| Caller | URL | Behavior |
|---|---|---|
| Wizard citation | `/documents/{documentId}` | Opens detail page directly |
| AdminMachineDetail linked doc row | `/documents/{documentId}` | Opens public detail |
| AdminMachineDetail (manufacturer context) | `/documents?manufacturer=Stern` | Pre-filtered list |
| Future manufacturer page | `/documents?manufacturer=Stern` | Pre-filtered list |

The `game` and `manufacturer` query params are the only v1 filter params. A `type` filter is out of scope for v1 but the URL scheme accommodates it naturally (`/documents?type=Manual&manufacturer=Stern`).

---

## Testing

| Test | Project | What it asserts |
|---|---|---|
| `DocumentListTests` (bUnit) | `PinballWizard.Web.Tests` | Game/mfr params initialize filter fields; empty state renders; admin columns visible when `IsAdmin=true`, hidden when false; row click navigates to correct detail URL |
| `DocumentDetailTests` (bUnit) | `PinballWizard.Web.Tests` | Provenance card fields render correctly; admin panel visible/hidden per `IsAdmin`; not-found state renders; "Open document" CTA has correct href |
| `CrossPartitionQueryAllowListTests` | `PinballWizard.Infrastructure.Tests` | `StreamDocumentsAsync` is on the allow-list |
| `DocumentListItemProjectionTests` | `PinballWizard.Infrastructure.Tests` | Cosmos projection maps all DTO fields correctly |
| Accessibility | `PinballWizard.Web.Tests` | `[Fact(Skip = "Accessibility")]` category for both pages |

---

## Out of Scope (v1)

- Free-text search on document title / link text (v2)
- `type` filter on the list page (v2)
- PDF preview / inline viewer
- Download tracking / click analytics
- Server-side cursor pagination (v2 if corpus grows past ~500 docs)
- Wizard citation link changes (follow-up ticket)
