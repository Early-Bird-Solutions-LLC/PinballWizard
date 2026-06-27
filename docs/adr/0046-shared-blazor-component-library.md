# 0046 — Shared Blazor component library in `Components/Shared/`

**Status:** Accepted
**Date:** 2026-06-27

## Context

As the admin section grew to 14+ pages, five high-frequency structural patterns
emerged as inline copy-paste: page headers, empty states, error alerts, data grids,
and status chips. The grid pattern alone appeared nine times across admin pages,
with behavioural parameters (`Dense`, `Hover`, `Striped`, `RowsPerPage`,
`Elevation=2`) duplicated at every call site.

A secondary problem: two grid components were in use. `MudDataGrid` (four admin
pages) and `MudTable` (three admin pages + two embedded tables) differ in API
surface but produce the same visual output for these use cases. One page
(`AdminCorpus`) used raw `<thead><tr><th>` HTML inside `MudSimpleTable`,
violating ADR-0008 (MudBlazor strict). Two public pages (`About`, `Status`)
had heading typography drift from the admin standard.

MudBlazor does not support theme-level behavioural defaults for these parameters
— `Dense`, `RowsPerPage`, `Hover`, and `Striped` are component-level C# params,
not CSS. Wrapper components are the only mechanism for enforcing consistent
defaults while keeping call sites free of boilerplate.

## Decision

Extract repeated patterns into a `Components/Shared/` library. Standardise on
`MudDataGrid` and migrate all `MudTable` and `MudSimpleTable` pages to the
wrapper. The shared layer covers both admin and public pages.

**Components:**

| Component | Pattern wrapped | Baked-in defaults |
| --- | --- | --- |
| `AppDataGrid<TItem>` | `MudDataGrid` | Hover, Striped, Dense, Elevation=2, RowsPerPage=25 |
| `AppPageHeader` | breadcrumbs + h4 + body2 block | h4 heading, body2 subtitle, mb-6 |
| `AppEmptyState` | centered icon + two-line message | Inbox icon, py-8 stack |
| `AppErrorAlert` | `MudAlert Severity.Error` | mb-4 |
| `AppStatusChip` | `MudChip T="string"` | Size.Small, Variant.Filled |
| `AppBulletList` | `MudList Dense` | Dense=true |
| `AppBulletItem` | `MudListItem Icon=Circle` | Circle icon, body2 |
| `AppSummaryCard` | `MudCard` + header + actions | Elevation=2 |

Call-site flexibility is preserved via `[Parameter]` overrides and `@attributes`
splatting for pass-through props (Groupable, RowClick, data-testid, etc.).

`AdminLoadingBar` and `AdminCountValue` (previously in `Components/Pages/Admin/`)
move to `Components/Shared/` to reflect their actual reuse scope.

## Consequences

**Positive:**
- New pages pick up consistent behavioural defaults without any per-page
  boilerplate. Drift requires an intentional override, not an omission.
- The ADR-0008 violation (`AdminCorpus` raw HTML) is resolved.
- About and Status page heading typography are normalised to the admin standard.
- `MudTable` and `MudSimpleTable` are eliminated from the page layer; only
  `AppDataGrid` (wrapping `MudDataGrid`) is used.

**Negative / trade-offs:**
- Pages that need `AppDataGrid` behaviours not covered by the wrapper (e.g.
  multi-axis grouping on AdminMachines) pass them as splatted attributes, which
  are invisible to the compiler. The risk is low given the small number of
  such cases and the test coverage on each page.
- Moving `AdminLoadingBar` and `AdminCountValue` requires updating `@using`
  references across ~8 call sites.

**Neutral:**
- `PinballTheme.cs` is unchanged — palette, typography, and shape tokens are
  already correct and consistent.
- Status page cards intentionally keep `Elevation=1` (lower than admin cards)
  as a hierarchy signal; this is documented intent, not drift.
