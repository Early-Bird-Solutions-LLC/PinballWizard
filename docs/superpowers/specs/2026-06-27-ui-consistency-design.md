# UI Consistency: Shared Component Library

**Date:** 2026-06-27
**Branch:** feat/ui-shared-components (planned)
**Status:** Approved for implementation

---

## Problem

The PinballWizard web app has grown to 14+ pages sharing five high-frequency structural patterns — page headers, empty states, error alerts, data grids, and status chips — as inline copy-paste. The grid pattern alone appears nine times across admin pages using two different MudBlazor grid components (`MudDataGrid` and `MudTable`), with behavioural parameters (`Dense`, `Hover`, `Striped`, `RowsPerPage`, `Elevation`) duplicated at every call site. One page (`AdminCorpus`) uses raw `<thead><tr><th>` HTML inside `MudSimpleTable`, violating ADR-0008 (MudBlazor strict). Two public pages (`About`, `Status`) have heading typography drift relative to admin pages.

This creates boilerplate maintenance burden, visual drift risk on new pages, and a code-reviewer surface that a prospective customer should not have to read.

---

## Goal

Extract repeated MudBlazor patterns into a `Components/Shared/` library of eight components (seven primary wrappers + one companion). Standardise on `MudDataGrid` (migrate all `MudTable` pages). Eliminate the ADR-0008 violation. Normalise public page heading typography.

Shared components cover both admin and public pages — consistency is app-wide.

---

## Scope

### Components (new)

All land in `src/PinballWizard.Web/Components/Shared/`.

| Component | Pattern replaced | Pages |
|---|---|---|
| `AppDataGrid<TItem>` | `MudDataGrid` copy-paste + all `MudTable` pages + raw-HTML corpus table | 9 |
| `AppPageHeader` | breadcrumbs + `MudText h4` + `MudText body2` block | 8 admin + 2 public |
| `AppEmptyState` | `MudStack > MudIcon + MudText + MudText` centered block | 7 |
| `AppErrorAlert` | `MudAlert Severity.Error Class="mb-4"` | 8 |
| `AppStatusChip` | `MudChip T="string" Size.Small` | 5 |
| `AppBulletList` | `MudList Dense + MudListItem Icon=Circle` | About (3×) |
| `AppBulletItem` | `MudListItem Icon=Circle + MudText body2` | About |
| `AppSummaryCard` | `MudCard Elevation=2 + MudCardContent + MudCardActions` | AdminDashboard (6×) |

### Pages migrated

**MudDataGrid → `AppDataGrid` (drop-in):**
- `AdminSources`, `AdminDocumentTriage`, `AdminLinkOverrides`, `AdminMachines`

**MudTable → `AppDataGrid` (column rewrite):**
- `AdminManufacturers` — `MudTableSortLabel` → `PropertyColumn Sortable=true`
- `AdminJobs` — custom row template → `TemplateColumn`; wrapping `MudPaper` removed
- `AdminJobDetail` — execution history table → `AppDataGrid ShowPager=false`
- `AdminMachineDetail` — documents table → `AppDataGrid RowsPerPage=25`

**MudSimpleTable + raw HTML → `AppDataGrid` (fixes ADR-0008 violation):**
- `AdminCorpus` — doc-type table → `AppDataGrid ShowPager=false`
- `AdminSourceDetail` — run history → `AppDataGrid ShowPager=false`

**Header normalisation:**
- `About` — subtitle `Typo.body1` → `Typo.body2` (drift fix); use `AppPageHeader`
- `Status` — inconsistent `mt-6 mb-1` margin → use `AppPageHeader`

**Summary card extraction:**
- `AdminDashboard` — 6 inline `MudCard` blocks → 6 `<AppSummaryCard>` invocations

### Out of scope

- Theme palette, typography, or shape — `PinballTheme.cs` is correct as-is
- Landing sub-components (`LandingHero`, `SeedQuestionGrid`, `FeaturedMachinesStrip`, `ArchitectureStoryStrip`) — delight surface, no repeated boilerplate
- Status page card elevation (`Elevation=1`) — intentionally lower than admin cards; document as deliberate, not drift

---

## Component APIs

### `AppDataGrid<TItem>`

Bakes in: `Hover=true`, `Striped=true`, `Dense=true`, `Elevation=2`.

```razor
@typeparam TItem

[EditorRequired] Items             IEnumerable<TItem>
[EditorRequired] Columns           RenderFragment          // <Columns> slot
                 RowsPerPage       int               = 25
                 ShowPager         bool              = true  // false for embedded detail tables
                 NoRecordsContent  RenderFragment?          // optional; caller-supplied empty state
                 + @attributes splatted                     // Groupable, GroupExpanded, RowClick,
                                                            // data-testid, etc.
```

`<MudDataGridPager T="TItem" />` is rendered inside `<PagerContent>` when `ShowPager=true`. When `ShowPager=false` no pager is rendered and no wrapping `MudPaper` is needed (elevation is baked into the grid itself).

Pages that use `RowsPerPage=50` (AdminMachines) or need `Groupable` / `GroupExpanded` / `RowClick` pass those as splatted attributes. The no-records content defaults to nothing (caller uses `AppEmptyState` outside the grid, controlled by their own `_isEmpty` flag, as is the existing pattern).

### `AppPageHeader`

```razor
[EditorRequired] Title          string
                 Subtitle       string?                     = null   // Typo.body2, Color.Secondary, mb-6
                 Breadcrumbs    IReadOnlyList<BreadcrumbItem>? = null
                 Actions        RenderFragment?             = null   // right-side slot for header buttons
```

When `Actions` is provided, title and actions sit in a `MudStack Row AlignItems.Center`. Breadcrumbs render as `<MudBreadcrumbs Class="pa-0 mb-4" />`. Heading is always `Typo.h4 GutterBottom` (normalises the `mt-6 mb-1` / `mt-6 mb-2` drift on Status and About).

### `AppEmptyState`

```razor
[EditorRequired] Heading        string
                 Detail         string?                     = null
                 Icon           string                      = Icons.Material.Outlined.Inbox
                 DetailContent  RenderFragment?             = null   // for <code>-containing hints
                 + @attributes splatted                              // data-testid
```

AdminDocumentTriage passes `Icon="@Icons.Material.Outlined.CheckCircle"`. Pages with `<code>` in the hint (AdminSources, AdminMachines) use `DetailContent`.

### `AppErrorAlert`

```razor
[EditorRequired] ChildContent   RenderFragment
                 Class          string                      = "mb-4"
                 + @attributes splatted                              // data-testid
```

The `ChildContent` slot supports inline markup (`<code>`, `<em>`) as some pages (AdminJobs) include ARM error context.

### `AppStatusChip`

```razor
[EditorRequired] Color          Color
[EditorRequired] ChildContent   RenderFragment              // label text
                 Variant        Variant                     = Variant.Filled
                 + @attributes splatted                              // data-testid
```

Bakes in: `Size.Small`, `T="string"`. Standardises on `Variant.Filled` — previously inconsistent across pages (some omitted Variant, getting MudBlazor's default outlined shape).

### `AppBulletList`

```razor
[EditorRequired] ChildContent   RenderFragment              // <AppBulletItem> children
                 Class          string?                     = null
                 + @attributes splatted
```

Wraps `MudList T="string" Dense=true`.

### `AppBulletItem`

```razor
[EditorRequired] ChildContent   RenderFragment              // MudText body2 or MudLink
                 Icon           string                      = Icons.Material.Filled.Circle
```

### `AppSummaryCard`

```razor
[EditorRequired] Icon           string
[EditorRequired] IconColor      Color
[EditorRequired] Label          string
[EditorRequired] ActionHref     string
[EditorRequired] ActionLabel    string
[EditorRequired] Content        RenderFragment              // count, status widget, etc.
                 Caption        string?                     = null   // tertiary line below label
                 Class          string                      = "admin-summary-card"
```

Collapses AdminDashboard's 6 verbatim `MudCard` blocks to 6 `<AppSummaryCard>` invocations.

---

## Migration approach

1. Create all seven new components in `Components/Shared/` first (no call sites changed yet)
2. Add bUnit tests for `AppDataGrid`, `AppPageHeader`, `AppEmptyState`, `AppErrorAlert`, `AppStatusChip`
3. Migrate MudDataGrid pages one at a time (AdminSources first — simplest shape)
4. Migrate MudTable pages (AdminManufacturers, AdminJobs, AdminJobDetail, AdminMachineDetail)
5. Migrate MudSimpleTable pages (AdminCorpus, AdminSourceDetail — ADR-0008 fix)
6. Migrate page headers across all pages
7. Migrate empty states and error alerts across all pages
8. AdminDashboard: extract `AppSummaryCard`, migrate all 6 cards
9. About page: migrate bullet lists, fix subtitle typography
10. Move existing `AdminLoadingBar` and `AdminCountValue` from `Components/Pages/Admin/` to `Components/Shared/` (they're already shared in practice — used by multiple admin pages)

---

## Tests

Each new shared component gets its own test file in `tests/PinballWizard.Web.Tests/Components/Shared/`. Test patterns follow the existing `AsyncBunitContext` conventions:

- `AppDataGrid` — renders columns, respects RowsPerPage default, hides pager when ShowPager=false, splats data-testid
- `AppPageHeader` — renders title only, with subtitle, with breadcrumbs, with Actions slot, heading is always h4
- `AppEmptyState` — renders heading, renders detail, renders DetailContent slot, renders custom icon
- `AppErrorAlert` — renders ChildContent, applies Class default, splats data-testid
- `AppStatusChip` — renders label, applies Variant.Filled default, applies Size.Small

Pages that migrate away from inline patterns keep their existing tests unchanged — the behavioural assertions (what the page shows) don't change, only what component the page uses internally.

---

## File moves

`AdminLoadingBar.razor` and `AdminCountValue.razor` move from `Components/Pages/Admin/` to `Components/Shared/`. All existing `@using` references update accordingly. These components have no `Admin`-only semantics; moving them reflects their actual reuse scope.

---

## Non-goals

- No new CSS or theme changes (MudBlazor behavioural params can't be set via CSS; visual tokens are already correct in `PinballTheme.cs`)
- No public landing page refactoring (different structural concerns — delight surface vs data grid)
- No `AdminCorpus` logic changes — only the table markup changes from raw HTML to `AppDataGrid`
- No new features or behaviour changes on any migrated page

---

## Definition of done

- All seven new components exist in `Components/Shared/`
- All `MudTable` pages migrated to `AppDataGrid`
- All `MudSimpleTable` + raw HTML markup replaced (ADR-0008 violation cleared)
- All page headers use `AppPageHeader` (About subtitle typography normalised)
- All error alerts use `AppErrorAlert`
- All empty states use `AppEmptyState`
- All status chips use `AppStatusChip`
- AdminDashboard uses `AppSummaryCard`
- About bullet lists use `AppBulletList` / `AppBulletItem`
- `AdminLoadingBar` and `AdminCountValue` moved to `Components/Shared/`
- New shared component tests pass
- Existing page tests pass unchanged
- `/local-review` and `/standards-audit` pass
