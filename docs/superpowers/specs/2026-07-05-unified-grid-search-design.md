# Unified AI grid search across admin + public pages

**Date:** 2026-07-05
**Branch:** `feat/unified-grid-search`
**Status:** Design — approved by user
**Related:** commit `7ccc4e9` (original GridSearch feature), commit `74bd102` (GridSearch DI
fix that accidentally reverted 3 pages to raw `MudDataGrid`), commit `5870325` (removed the
`Franchise` column/axis from `AdminMachines`, which the GridSearch agent prompt still
references)

## Problem

The AI-driven "GridSearch" feature (`Components/Shared/GridSearch.razor`, baked into
`Components/Shared/AppDataGrid.razor` via `EnableAiSearch=true`) was introduced in `7ccc4e9` as
an AI-showcase capability: a natural-language box that asks an LLM to translate a query into
structured grid filters. It never finished rolling out and has since drifted:

1. **Inconsistent across pages.** Three pages (`AdminDocumentTriage`, `AdminJobs`,
   `AdminMachines`) had it removed by `74bd102` — confirmed accidental. Several others
   (`AdminManufacturers`, `AdminSources`, `AdminJobDetail`, `AdminLinkOverrides`, `AdminCorpus`)
   never had it at all, still on raw `MudDataGrid` with hardcoded page sizes.
2. **`DocumentList.razor`** (backs both `/documents` and `/admin/documents` — the page in the
   reported screenshot) has GridSearch **and** a redundant legacy "Search by game…" text field
   plus Manufacturer/Type chip filters — two competing filter mechanisms stacked on one page.
3. **Page size inconsistency.** `IUserPreferencesService.PageSize` already defaults to 10, but
   `AdminManufacturers` and `AdminSources` hardcode `RowsPerPage="25"`, silently overriding the
   shared preference.
4. **Stale agent prompt.** `Ai/Agents/GridSearch.md` documents only 3 grid contexts
   (`admin-machines`, `admin-jobs`, `admin-document-triage` — exactly the 3 pages `74bd102`
   reverted, confirming the feature was left mid-flight). It also still lists a `Franchise`
   column for `admin-machines`, which `5870325` deleted from `MachineCatalogRow` today — a
   query like "Godzilla franchise games" would generate a filter on a nonexistent property,
   which `AppDataGrid.FilterFunc`'s reflection lookup silently no-ops
   (`if (prop == null) continue;`), producing a confident-sounding but wrong "0 results"
   experience.
5. **Semantic search is a dead branch.** The agent prompt already recognizes conceptual queries
   ("games with a sci-fi theme" — literally one of `GridSearch.razor`'s rotating placeholder
   examples) and returns `isSemanticSearch=true` + `semanticQuery`, but
   `AppDataGrid.HandleAiFilters` only reads `response.Filters` (empty for a semantic query) and
   never uses `SemanticQuery`. Typing the example placeholder text today silently returns every
   row, unfiltered, with an explanation claiming a search happened.

## Goals

- Every data grid in the app uses `AppDataGrid` (and therefore GridSearch), with one documented
  exception (`AdminSourceDetail`, ADR-0046 hierarchy-row incompatibility).
- `DocumentList` drops its legacy text/chip filters entirely; GridSearch is the only on-page
  interactive filter.
- `?manufacturer=`/`?game=`/`?type=` query params keep narrowing the server-side Cosmos fetch on
  `DocumentList` (preserves the `Manufacturers.razor` deep link and bounds read size); GridSearch
  filters client-side on top of whatever that fetch returned.
- Every grid defaults to `Prefs.PageSize` (10) — no more hardcoded per-page overrides.
- The agent prompt is corrected and extended to cover every grid context with an accurate
  schema.
- Semantic queries actually filter (keyword/substring match), not silently no-op.
- Dismissing the AI feedback banner actually clears the applied filter (currently doesn't).

## Non-goals

- True ML-embedding semantic search. The fix here is a scoped, generic keyword/substring match
  — real but modest. A real semantic/vector search is a separate, much larger initiative (would
  need an embedding call per query, a vector comparison against precomputed row embeddings or a
  live AI Search query) and is out of scope for this pass.
- Changing `AdminSourceDetail`'s grid. Its raw-`MudDataGrid` usage is a pre-existing, ADR-0046
  documented, technically-justified exception (`HierarchyColumn` + master-detail `ChildRowContent`
  aren't compatible with `AppDataGrid`'s attribute-splatting). Not touched.
- Any change to `AdminDashboard`, `AdminMonitoring`, `AdminRunDocuments`, `AdminSettings`,
  `AdminDocumentDetail`, `AdminMachineDetail`, `AdminJobExecutionDetail` — none of these render a
  searchable data grid (confirmed by inventory; `AdminJobExecutionDetail`'s search box is a
  log-line text search within one execution's output, an unrelated mechanism).
- Adding `Themes` to `MachineCatalogRow` for theme-aware semantic matching on `admin-machines`.
  Investigated during planning: `Themes` doesn't exist on `MachineStatEntry` (the Cosmos-persisted
  `catalog_stats` document schema) at all today — adding it means a schema change to that document
  plus its builder (`CosmosCatalogStatsRepository.MapMachine`), threading through `MachineDocStats`
  to `MachineCatalogRow`, AND a live `--build-catalog` re-run before any real data shows up in the
  admin UI. Too large and too live-data-coupled for this pass. The generic semantic match (§4)
  still works today using each row's existing string fields (e.g. `Title` often already contains
  the thematic word — "Godzilla", "Star Wars").

## Design

### 1. Page-by-page grid migration

| Page | Row type | `SearchContext` | Change |
|---|---|---|---|
| `AdminDocumentTriage` | `DocumentTriageRow` | `admin-document-triage` | Restore `AppDataGrid` (revert the accidental revert) |
| `AdminJobs` | `JobStatus` | `admin-jobs` | Restore `AppDataGrid` |
| `AdminMachines` | `MachineCatalogRow` | `admin-machines` | Restore `AppDataGrid` |
| `AdminManufacturers` | `ManufacturerRow` | `admin-manufacturers` | Migrate `MudDataGrid` → `AppDataGrid`; drop hardcoded `RowsPerPage="25"` |
| `AdminSources` | `IngestionSourceRow` | `admin-sources` | Migrate; drop hardcoded `RowsPerPage="25"` |
| `AdminJobDetail` | `JobExecution` | `admin-job-detail` | Migrate (currently no `RowsPerPage` override, so no page-size change, just adds search) |
| `AdminLinkOverrides` | `LinkOverrideRow` | `admin-link-overrides` | Migrate |
| `AdminCorpus` | `DocTypeChunkCount` | *(search disabled)* | Migrate with `EnableAiSearch="false"` — ~10-row stats table, pagination/styling consistency only |
| `AdminSourceDetail` | `ScrapeRunRecord` | — | **Unchanged** (documented exception, comment already explains why) |
| `DocumentList` (`/documents`, `/admin/documents`) | `DocumentListItem` | `admin-document-list` (IsAdmin) / `public-document-list` (else) | Remove legacy search box + chip filters (see §2) |
| `Manufacturers.razor` (public) | — | — | **Unchanged** — already on `AppDataGrid` |

For each migrated page, remove any hardcoded `RowsPerPage="N"` attribute entirely so the grid
falls through to `AppDataGrid`'s existing `RowsPerPage="@(RowsPerPage ?? Prefs.PageSize)"`
default — meaning every grid in the app honors one shared, user-adjustable preference (default
10) instead of scattered hardcoded values.

### 2. `DocumentList.razor` changes

Remove the `MudStack` containing the "Search by game…" `MudTextField` and the two `MudChipSet`
filters (Manufacturer, Type) entirely. The `Game`/`Manufacturer`/`Type` `[Parameter]`s,
`OnParametersSetAsync`'s call to `Repo.StreamDocumentsAsync(Game, Manufacturer, Type, IsAdmin,
token)`, and the `[SupplyParameterFromQuery]` bindings in `AdminDocuments.razor`/the public
`Documents.razor` page **stay exactly as they are** — they're the mechanism that keeps
`Manufacturers.razor`'s `/documents?manufacturer=X&game=Y` deep link working and keeps the
Cosmos read bounded to one manufacturer/game instead of the full corpus. Only the *visible,
interactive* filter UI is removed; GridSearch (via `AppDataGrid`) becomes the sole on-page way to
further narrow whatever set the query-string-scoped fetch returned.

`AppDataGrid Items="@_documents"` gets `SearchContext="@(IsAdmin ? "admin-document-list" :
"public-document-list")"` — two contexts because `DocumentListItem.LinkStatus` /
`LinkFailureReason` / `ResolutionStrategy` are "Admin-only — null on public projection" (per the
type's own doc comment); a public user's query like "failed links" would otherwise generate a
filter that silently matches nothing (see `ApplyOperator`'s null-value handling), a confusing
silent-empty-result experience. The public context's schema omits those three columns.

### 3. Agent prompt (`Ai/Agents/GridSearch.md`) corrections

- **Fix `admin-machines`:** remove the `Franchise` column (deleted from `MachineCatalogRow` in
  `5870325`). No replacement column — see the Themes note under Non-goals.
- **Add 6 new context sections**, each with the real column list pulled from the actual row
  record (not guessed):
  - `admin-manufacturers`: `Key`, `DisplayName`, `Enabled` (bool), `HasSource` (bool),
    `Machines` (int)
  - `admin-sources`: `Id`, `Name`, `SourceUrl`, `Enabled` (bool), `Cadence`, `LastRun`,
    `LastSuccess`, `DocsDiscovered` (int), `RunFailures` (int), `DiscoveryStatus`,
    `DiscoveryNotes`, `DiscoveryDate`
  - `admin-job-detail`: `ExecutionName`, `Status`, `StartOn` (datetime), `EndOn` (datetime)
  - `admin-link-overrides`: `SourcePattern`, `MachineIds`, `CreatedBy`, `CreatedAt`, `Notes`
  - `admin-document-list`: `DocumentId`, `Title`, `DocumentType`, `GameTitle`, `Edition`,
    `Manufacturer`, `FileFormat`, `PageCount` (int), `SizeBytes` (int), `FirstDiscoveredAt`
    (datetime), `LinkStatus`, `LinkFailureReason`, `ResolutionStrategy`
  - `public-document-list`: same as `admin-document-list` minus `LinkStatus` /
    `LinkFailureReason` / `ResolutionStrategy`
- `admin-jobs` and `admin-document-triage` sections are already accurate — no change.

### 4. Semantic search: generic keyword match

`AppDataGrid.HandleAiFilters` currently discards `IsSemanticSearch`/`SemanticQuery`. Fix:

- Store both on the component (`_isSemanticSearch`, `_semanticQuery`) alongside the existing
  `_currentFilters`.
- `FilterFunc(T item)`: when `_isSemanticSearch && !string.IsNullOrWhiteSpace(_semanticQuery)`,
  match via a **generic, reflection-based blob**: concatenate every public `string` property
  value and every public `IEnumerable<string>` property's joined values on `T` into one
  case-insensitive haystack, then `Contains(_semanticQuery)`. This is deliberately generic (same
  reflection approach `FilterFunc`/`ApplyOperator` already use for structural filters) so it
  works for any row type without per-page special-casing — `Title`, `Manufacturer`, `Edition`,
  etc. are picked up automatically on every existing row type, as would any future row type's
  string-ish properties, with no per-page wiring.
- When `_isSemanticSearch` is false, behavior is unchanged (existing structural filter loop over
  `_currentFilters`).
- This ships real, working semantic-ish matching immediately using only fields that already
  exist — e.g. "sci-fi themed games" still matches on `Title` words like "Godzilla" or
  "Star Wars" today. A true `Themes`-aware match is a deliberately deferred follow-up (see
  Non-goals) since it requires a Cosmos-persisted schema change (`MachineStatEntry` →
  `MachineDocStats` → `MachineCatalogRow`, three layers) plus a live `--build-catalog` re-run to
  populate existing catalog_stats documents — out of scope for this pass.

### 5. Bug fix: clearing feedback doesn't clear the filter

`GridSearch.razor`'s `ClearFeedback()` resets its own local `_lastResponse`/`_query` state but
never notifies the parent — `AppDataGrid._currentFilters` (and the new semantic state) stays
applied even after the user dismisses the explanation banner via its close (×) button, which
visually implies "undo this search." Fix: `ClearFeedback` becomes `async Task` and additionally
`await OnFiltersApplied.InvokeAsync(new GridSearchResponse([], "", false, null))` so the grid
un-filters when the banner is dismissed.

## Testing

GridSearch currently has **zero** test coverage anywhere in the repo (confirmed — only a DI-mock
registration in test infrastructure, no behavior tests). Given this project's testing standard
("tests assert behavior, not structure") and that this is an AI-showcase feature, this pass adds:

- **`GridSearchServiceTests`** (`PinballWizard.Application.Tests`): mock `IFoundryAgentFactory`
  to return canned agent text. Cover: well-formed JSON parses; markdown-fenced JSON
  (```` ```json ... ``` ````) is extracted correctly; non-JSON agent response returns an
  explanatory `GridSearchResponse` (not an exception) — degrade visibly, per this repo's
  no-masking-fallback invariant; the agent throwing returns an explanatory response and logs the
  exception.
- **`GridSearchClientTests`** (`PinballWizard.Web.Tests`): fake `HttpClient` handler. Cover:
  empty/whitespace query short-circuits without a call; successful response deserializes;
  non-success/exception returns an explanatory `GridSearchResponse` rather than throwing.
- **`AppDataGridTests`** (new, `PinballWizard.Web.Tests`) for the filtering logic in isolation
  (not tied to any one page): `ApplyOperator` for each operator (`contains`/`equals`/`gt`/`lt`/
  `ge`/`le`) including null-value edge cases; the new semantic-blob match (matches on a `Themes`
  -like property, matches on `Title`, case-insensitive, no match when the term isn't present
  anywhere); `ClearFeedback` round-trip clears applied filters via `OnFiltersApplied`.
- **Per-page bUnit smoke tests** for every migrated/restored page: extend or add to
  `AdminMachinesTests`, `AdminJobsTests`, `AdminDocumentTriageTests`, `AdminManufacturersTests`,
  `AdminSourcesTests`, `AdminJobDetailTests`, `AdminLinkOverridesTests` asserting the
  `[data-testid='grid-search-input']` renders. Add an `AdminCorpusTests` assertion that it does
  **not** render there (`EnableAiSearch="false"`).
- **`DocumentListTests`**: assert the old `doc-list-game-filter`/`doc-list-mfr-filter`/
  `doc-list-type-filter` test IDs no longer render; assert `grid-search-input` does; assert a
  `?manufacturer=X` query param still narrows what `IRawDocumentRepository.StreamDocumentsAsync`
  is called with (deep-link fetch-scoping preserved).
- **Page-size regression check**: a test (or extending an existing one) confirming a migrated
  grid's rendered `RowsPerPage` follows `Prefs.PageSize` rather than a hardcoded value, for at
  least one representative page (`AdminManufacturers`, the clearest before/after case).

## Out of scope / explicitly not doing

- True embeddings-based semantic search (see Non-goals).
- Any change to `AdminSourceDetail`'s grid.
- Any change to non-grid pages (dashboards, detail pages, log search).
- Persisting or seeding GridSearch's query from the `?manufacturer=`/`?game=`/`?type=` deep-link
  params into the search box itself — those params only affect server-side fetch scope, as
  today; GridSearch always starts empty on page load.
