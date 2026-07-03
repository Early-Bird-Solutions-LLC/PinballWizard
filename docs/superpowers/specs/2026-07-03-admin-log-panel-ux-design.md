# Admin Console-Log Panel UX — Design

**Date:** 2026-07-03
**Status:** Approved (brainstorming)
**Topic:** Scrollable fixed-height log panel + whole-run server-side search + load-more, on `AdminJobExecutionDetail`.

## Problem

The per-run console-log panel (`AdminJobExecutionDetail`) has three UX gaps on real runs
(e.g. a 23-minute twip run with thousands of lines):

1. **Unbounded height** — the log container (`overflow-x:auto`, no max-height) grows down the
   page with no scrollbar of its own.
2. **Hard 1000-line cap** — output is truncated at 1000 lines with no way to see more.
3. **Search is loaded-lines-only** — the existing "Filter lines…" box filters client-side over
   the loaded 1000 lines, so it silently cannot find matches later in the run.

## Decisions (locked)

- **Height:** fixed `max-height:60vh` + `overflow:auto` on the log container.
- **Search:** server-side KQL `contains` across the *whole* execution, debounced. Replaces the
  client-side filter (single search box, whole-run).
- **Load more:** a button that raises the line/match budget (+1000, ceiling 10000) and refetches
  in whichever mode (search / no-search) is active.

## Unified query model

One method, three behaviours, keyed off a `search` term and a `maxLines` budget:

| Mode | Query | Result |
| --- | --- | --- |
| no search | time-window, ascending, `take maxLines+1` | first N lines; `Truncated` if more |
| search "foo" | + `where Message contains @'foo'` across the run | first N matches; `Truncated` if more |
| load more | raise N (+1000, ≤10000), refetch | more of the active mode |

`contains` is case-insensitive (matches "search" expectations). The query's time window already
spans the whole execution, so search covers everything without pagination cursors.

## Architecture

### Unit 1 — KQL builder + escaper (`Infrastructure/Jobs`)

- `JobLogKql.BuildExecutionLogsQuery(executionName, startUtc, endUtc, maxLines, string? search)` —
  when `search` is non-empty, inject `| where Message contains @'<escaped>'` before `| order by`.
- **Security (crux):** the search term is the ONLY user-controlled value entering the KQL (job/
  execution names are ARM-supplied). Escape it as a **verbatim KQL string literal** (`@'…'`, every
  `'` doubled to `''`) + strip CR/LF + length-cap (200). Add `JobLogSafe.KqlLiteral(string?)` for
  this (distinct from the existing `Scrub`, which only strips CR/LF for log-forging).
  > **No-guessing gate (plan Task 1):** verify the verbatim-literal escaping rule (`@'…'` with
  > doubled single quotes, no other escapes needed) against the Kusto string-literal docs before it
  > is written into code. If the rule differs, use the verified form.
- Raise `MaxLinesCap` 1000 → 10000 (the new hard ceiling).

### Unit 2 — Reader + interface (`Application` + `Infrastructure`)

- `IJobLogReader.GetExecutionLogsAsync(jobName, executionName, startOn, endOn, maxLines, string? search, CancellationToken ct)` — add `search`.
- `LogAnalyticsJobLogReader`: normalize `search` (trim; empty/whitespace → null; length-cap), cap
  `maxLines` at `MaxLinesCap`, pass both to the KQL builder. `JobLogResult` (Lines + Truncated)
  unchanged; `Truncated` now means "more than `maxLines` lines/matches".

### Unit 3 — Page UX (`AdminJobExecutionDetail.razor`)

- **Height/scroll:** log container `Style="max-height:60vh;overflow:auto;font-family:monospace"`.
- **Search:** the "Filter lines…" box becomes a **debounced** `MudTextField` (~400 ms;
  `DebounceInterval` + `OnDebounceIntervalElapsed`) that re-queries server-side with `_search`.
  Remove the client-side `_filter` / `VisibleLines` (the server filters now). A small spinner shows
  while a search/load query is in flight (`_logBusy`).
- **Load more:** new `_maxLines` field (starts 1000). A "Load more" button renders under the panel
  when `_logResult.Truncated`; each click raises `_maxLines` by 1000 (≤10000) and refetches with the
  current `_search`.
- **Adaptive messaging:**
  - truncated + search → *"Showing the first N matches — refine your search or load more."*
  - truncated + no search → *"Showing the first N lines — output was truncated."*
  - zero results + search → distinct empty state *"No lines match '<term>'."* (`data-testid="exec-log-nomatch"`), NOT the "no output captured" state.
- **Live compose:** the auto-refresh loop passes `_maxLines` + `_search` so search / load-more work
  with a running job.
- Re-query runs through a single `LoadLogsAsync(int maxLines, string? search)` helper used by initial
  load, debounced-search, load-more, and auto-refresh.

### Unit 4 — Tests

- `JobLogKqlTests`: search clause present with escaped literal (`'` → `''`); no-search query
  unchanged; `take maxLines+1`.
- `JobLogSafeTests` (or extend): `KqlLiteral` doubles quotes, strips CR/LF, length-caps.
- `LogAnalyticsJobLogReaderTests`: `search` passthrough; `maxLines` capped at 10000; empty search →
  no where-clause; existing tests updated for the new signature.
- `AdminJobExecutionDetailTests` (bUnit): search box triggers a server re-query with the term;
  "Load more" appears when `Truncated` and raises `maxLines` + re-queries; container carries
  `max-height`; zero-match search renders `exec-log-nomatch`.

## Files

| File | Change |
| --- | --- |
| `src/PinballWizard.Infrastructure/Jobs/JobLogSafe.cs` | add `KqlLiteral(string?)` |
| `src/PinballWizard.Infrastructure/Jobs/JobLogKql.cs` | `search` param + `contains` clause; `MaxLinesCap` → 10000 |
| `src/PinballWizard.Application/Jobs/IJobLogReader.cs` | add `search` param |
| `src/PinballWizard.Infrastructure/Jobs/LogAnalyticsJobLogReader.cs` | normalize + pass `search`; cap |
| `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor` | height/scroll, debounced server search, load-more, adaptive messaging, `LoadLogsAsync` |
| `tests/…/Jobs/JobLogKqlTests.cs`, `JobLogSafeTests.cs`, `LogAnalyticsJobLogReaderTests.cs` | unit |
| `tests/…/Components/Admin/AdminJobExecutionDetailTests.cs` | bUnit |

## Constraints

- Personal-identity commits (`94459922+jkeeley2073@users.noreply.github.com`); no Claude attribution.
- MudBlazor-strict; theme tokens only (`Color.*`); the panel stays `@rendermode InteractiveServer`.
- Invariant #17: search/failed/empty/no-match each render a distinct honest state.
- Zero-warning build (`dotnet build PinballWizard.slnx -warnaserror`).
- Delivered from the `feat/admin-log-panel-ux` worktree.

## Non-goals

- No pagination cursors / infinite scroll (load-more button instead).
- No regex/structured search — plain case-insensitive substring (`contains`).
- No auth/posture change; the panel remains admin-gated behind `AdminActionGuard`.
