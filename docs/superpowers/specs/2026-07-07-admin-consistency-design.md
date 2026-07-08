# Admin Consistency & Delight Pass — Design

**Date:** 2026-07-07
**Status:** Draft (awaiting review)
**Source:** `Footer positioning best practice.zip` → `design_handoff_admin_consistency/` (`Admin.dc.html` + `README.md`)
**Scope:** `/admin/*` surface consistency, plus the public sticky-footer fix the zip is named for.

---

## 1. Context & the "is it deployed?" resolution

The handoff bundle is a high-fidelity HTML mockup (`Admin.dc.html`) proposing a 10-item
consistency pass over the admin surface. It reads as a from-scratch redesign, but most of the
target state already exists in the app.

The apparent discrepancy was traced to a **deploy outage**: ~19 consecutive `Deploy` runs failed
from ~Jul 6 through Jul 7 15:20 UTC (the `.dockerignore`-excludes-`docs/` bug, fixed in #720/#721),
so the live site was frozen on a pre-outage build for roughly a day. The pipeline recovered at the
Jul 7 19:20 UTC deploy. **The live admin now equals `main`** — confirmed against `main` (via
per-item source audit) and against fresh screenshots of the recovered live site.

Consequently the handoff is **not** "stale screenshots of already-fixed things." A few items are
genuinely already done; most are real, not-yet-built work. This spec scopes the work off `main`,
not off the mockup.

### Item-by-item reality (audited against `main` = live)

| # | Item | State | Action |
|---|---|---|---|
| 1 | Shared app shell | Mostly done — all 16 admin pages use `AdminLayout`; 10/16 use `AppPageShell` | Converge the 6 stragglers |
| 2 | Collapsible sidebar | Gap — `AppNavRail` Mini exists but is disabled in admin (`ShowToggle=false`) for circuit safety | Add **CSS/JS** collapse (no Blazor island) |
| 3 | Sidebar status footer | Gap — `AppNavRail` has no bottom slot | Add pinned status strip |
| 4 | No admin page footer | **Already true** | None (admin). Public footer handled separately — see §7 |
| 5 | One table grammar | **Done** — all 6 list pages use `AppDataGrid` | None |
| 6 | Status-color semantics | Gap — amber-as-status, blue, teal, 4 divergent link-status helpers | Enforce closed palette |
| 7 | Empty/loading pattern | Partial — `AppEmptyState` used widely; Monitoring uses static `…`/`—` | Add pulsing skeletons |
| 8 | Manufacturers page | **Already done** (since PR #537) | None |
| 9 | Column trims | Gap | Drop Documents "Format"; fold Triage "Document ID" |
| 10 | Comma number formatting | Gap — `AdminCountValue` renders raw ints | `N0` formatting |

**Already done:** #4 (admin), #5, #8. **Real work:** #1 (partial), #2, #3, #6, #7, #9, #10, plus the
public sticky footer.

---

## 2. Locked decisions

1. **Collapse via CSS + tiny vanilla JS, not a Blazor interactive island.** A live `@onclick`
   toggle in the static admin shell is the documented "hamburger bug" that broke admin-page
   hydration (`AdminLayout` header comment; memory
   `reference_interactive_island_static_layout_circuit_break`). The collapse toggles a CSS class and
   persists to `localStorage` in a few lines of JS — zero change to the admin pages' circuit
   profile.
2. **Include the public sticky-footer fix** (the zip's namesake). The specific `Footer
   Placement.dc.html` design is *not* in this bundle, so apply the conventional flexbox
   min-height layout. See §7.
3. **Closed 5-role color palette** (see §4.1). Amber is interactive-only.
4. **Build-identity string comes from a real source** (git SHA + build timestamp injected at image
   build), never a hardcoded `v2.4.1`. See §5.2.
5. **No theme change.** The live admin already renders in the light "Daytime/Paper" palette via the
   `ThemeService` CSS-variable layer; the mockup's light look is already the reality. The
   `AdminLayout` `MudThemeProvider` `IsDarkMode` setting is left untouched.

---

## 3. Delivery — 5 thematic PRs (Approach A)

Grouped to avoid same-file collisions (#6/#9/#10 all touch `AdminDocumentTriage` + `DocumentList`)
and to keep each diff independently reviewable and deployable. Default order 1→5; #3 and #5 are the
safest and may be pulled forward for quick wins.

Each PR independently clears `/local-review`, `/standards-audit`, the CI-equivalent test suite, and
a green post-merge `Deploy`.

### PR 1 — Data-surface consistency (#6, #9, #10)
The meatiest PR; the visible payoff on every list page and the dashboard.

### PR 2 — Sidebar collapse + status footer (#2, #3)
Shell chrome. Introduces the `BuildInfo` provenance surface.

### PR 3 — Monitoring loading states (#7)
Pulsing skeletons for genuinely-loading metrics; leave final states as text.

### PR 4 — Shell convergence (#1)
Move the 6 bypass pages onto `AppPageShell`/`AppPageHeader`.

### PR 5 — Public sticky footer (namesake)
`MainLayout` flexbox so `BrandFooter` pins to the viewport bottom on short pages.

---

## 4. PR 1 — Data-surface consistency (#6, #9, #10)

### 4.1 Closed color palette (the canonical rule)

| Role | Color | Meaning | Applies to |
|---|---|---|---|
| Green | `Color.Success` | success / healthy / active / succeeded / OK | status badges |
| Red | `Color.Error` | failure / missing / refused / not-in-catalog | status badges |
| Neutral | `Color.Default` | informational / unknown / suppressed / non-status tag (`platform_generic`, `Unknown`, `SUPPRESSED`) | status badges |
| Amber | `Color.Primary` | **interactive only** — links, buttons, active nav, primary CTAs | never a status badge |
| — | (banned) | `Color.Info`/blue and `Color.Tertiary`/teal are not in the palette | — |

### 4.2 #6 — Status-color fixes (audited sites)

**Introduce a single shared helper** `DocumentLinkStatusColor` (in `Components/Shared/`, analogous to
`JobStatusColor.cs`) handling both PascalCase enum names and snake_case Cosmos values. Replace the
4 divergent private `LinkStatusColor()` copies in `AdminDocumentTriage.razor` (269-275),
`MachineDetail.razor` (347-354), `DocumentDetail.razor` (242-248), `DocumentList.razor` (165-171).

Recolor sites to the closed palette:
- `JobStatusColor.cs:10` `Running`/`Processing` `Info`→ **`Success`** (active/healthy). `:12-13`
  `Stopped`/`Degraded` `Warning`→ **`Error`** (failed/degraded state).
- `CatalogHealthColors.cs:17-18` `NoManual`/`EditionGap` `Warning`→ **`Default`** (informational
  health flags, not failures).
- `SourceStatusView.cs:30` `Deferred` `Warning`→ **`Default`** (informational).
- `AdminDocumentTriage.razor:272-273` `NotInCatalog` `Warning`→ **`Error`**; `PlatformGeneric`
  `Info`→ **`Default`**.
- `platform_generic` `Warning`→ **`Default`** in `MachineDetail.razor:350`, `DocumentDetail.razor:246`,
  `DocumentList.razor:169` (folded into the shared helper).
- 5 `MudAlert Severity.Info` → **`Severity.Warning`** (or neutral): `AdminMachineDetail.razor:26`,
  `AdminJobs.razor:51`, `AdminJobDetail.razor:50`, `AdminJobExecutionDetail.razor:66,85`.

**`AppSummaryCard` CTA decoupling** (`AppSummaryCard.razor:18`): the CTA button currently inherits
`IconColor`, so non-Primary icon colors produce gray/teal/amber/blue CTAs. Add a distinct button
color that is **always `Color.Primary`** (decorative icon color may remain expressive, but the CTA
is always amber). Fix the `AdminDashboard.razor` cards accordingly (lines 63, 75, 92, 107 currently
`Secondary`/`Tertiary`/`Warning`/`Info`).

> Note: `AdminJobExecutionDetail.razor:354` log-line severity coloring (`Warning`→amber text) is a
> console-log convention, **not** a status badge — left as-is.

### 4.3 #9 — Column trims
- `DocumentList.razor:67` — delete the `Format` `PropertyColumn` (every row is `html`; no value).
- `AdminDocumentTriage.razor:57` — remove the standalone `Document ID` column; render the id as a
  `Typo.caption` secondary line inside the existing Link Text cell (8 cols → 7).

### 4.4 #10 — Comma formatting
- `AdminCountValue.razor:29` — `@Count` → `@Count?.ToString("N0")` (propagates to all 6 dashboard
  cards).
- Raw numeric list columns → `N0`: `AdminManufacturers` Machines (89), `AdminMachines` DocCount
  (113), `AdminSources` DocsDiscovered (91) + RunFailures (92), `DocumentList` PageCount (68).

### 4.5 Tests (behavior, not structure)
- `DocumentLinkStatusColor` mapping table incl. `NotInCatalog`→Error, `platform_generic`→Default,
  in both PascalCase and snake_case.
- A test asserting **no admin status chip resolves to `Color.Primary`/`Warning`/`Info`/`Tertiary`**
  (guards the closed palette against regression).
- `AdminCountValue` renders `30875` as `30,875`.

---

## 5. PR 2 — Sidebar collapse + status footer (#2, #3)

### 5.1 #2 — CSS/JS collapse
- Add a collapse control to `AppNavRail` (or an admin-specific wrapper) that toggles a
  `nav-rail--collapsed` class on the rail container and persists to `localStorage`
  (`pinwiz.admin.nav.collapsed`) via a small JS module. **No `@onclick`, no `@rendermode` change** on
  the admin layout subtree — the toggle is a plain `<button>` wired by the JS module, so the admin
  pages' static circuit profile is unchanged.
- Collapsed rail: 64px icon-only (labels hidden via CSS), `title` tooltips retained for a11y.
  Expanded: 260px.
- Restore persisted state on load before first paint (avoid flash) — inline the read in the JS
  module executed early, toggling the class on the container.

### 5.2 #3 — Sidebar status footer + `BuildInfo` provenance
- A pinned bottom strip in the rail: green dot + environment label + build identity in mono. Uses
  the closed palette (green dot = `Success`). Collapses to just the dot when the rail is collapsed.
- **Real source (no hardcoded string):**
  - `deploy.yml` passes `--build-arg BUILD_SHA=${{ github.sha }} --build-arg BUILD_TIME=<iso8601>` to
    the web image build.
  - `src/PinballWizard.Web/Dockerfile` declares those `ARG`s and promotes them to `ENV`
    (`PINWIZ_BUILD_SHA`, `PINWIZ_BUILD_TIME`).
  - A typed `BuildInfo` (bound from `IConfiguration`) exposes `Sha` (short), `BuildTimeUtc`, and
    `Environment` (from `IHostEnvironment.EnvironmentName`). Injected into the footer component.
  - Local dev (no build args) degrades visibly per Invariant #17: shows `local · dev`, not a fake
    version.
- **Format:** `● {Environment} · build {shortSha} · deployed {BuildTimeUtc:MMM d, HH:mm} UTC`.

> **Open decision for review:** the mockup shows a semver (`v2.4.1`). There are **0 git tags**, so no
> semver exists. Default here is the honest git-SHA identity. If a human-facing `vX.Y.Z` is wanted,
> we adopt git-tag-based `git describe` versioning (a small separate change). **Recommend: ship
> SHA+timestamp now; add semver later only if desired.**

### 5.3 Tests
- Circuit-safety: the interactive admin pages (Settings, Triage, LinkOverrides, Machines) still
  hydrate with the collapse control present (browser-gated ui-tests, per
  `reference_circuit_tests_ci_only`).
- `BuildInfo` falls back to `local`/`dev` when env vars are absent (no synthetic version).

---

## 6. PR 3 — Monitoring loading states (#7)

`AdminMonitoring.razor` currently shows a static `…`/`—` while telemetry loads.
- Loading tiles → pulsing `MudSkeleton` (Rectangle, pulse animation): Answer Latency P95 (96-99),
  5xx Error Rate (131-134), Refusal Rate (188-191); pipeline rows Lease lag (314-318), Dead letters
  (326-329), Reconcile drift (349-353) via the `PipelineCount` path.
- **Leave as plain text** (genuinely-final, not loading): Daily AI Cost "eval-only" (159-178),
  Short-circuits "expected" (338-341).
- Skeleton swaps to the real value with no extra transition once data resolves.
- Test: a loading-state render shows skeletons on the 3 tiles + 3 rows and **not** on Daily AI
  Cost / Short-circuits.

---

## 7. PR 4 — Shell convergence (#1)

Move the 6 bypass pages onto the shared shell so spacing/width/breadcrumbs move together:
- `AdminDashboard`, `AdminMonitoring`, `AdminSettings` — replace raw `MudContainer` + `AppPageHeader`
  with `AppPageShell`.
- `AdminMachineDetail`, `AdminJobExecutionDetail` — replace hand-rolled `MudBreadcrumbs` with
  `AppPageShell`/`AppPageHeader` (keep the post-load conditional-header pattern already used by
  `AdminJobDetail`/`AdminSourceDetail`).
- `AdminDocumentDetail` — wrap the bare `DocumentDetail` in the shell.
- Verify no header-cluster collision at realistic widths (the mockup's `flex:0 0 auto; nowrap` +
  min-width intent) — apply to the `AdminLayout` app-bar cluster if the audit shows compression.

---

## 8. PR 5 — Public sticky footer (namesake)

On short public pages (`/status`, `/about`) `BrandFooter` floats mid-viewport with empty space
below. Apply the conventional sticky-footer layout in `MainLayout`: a flex column with `min-height:
100dvh` on the layout wrapper and the content region `flex: 1`, so the footer sits at the viewport
bottom when content is short and flows naturally when content is tall. No change to `BrandFooter`
itself.
- Test/verify: `/status` (short) footer at viewport bottom; a long page (e.g. an Engineering doc)
  footer still flows after content (no overlap, no double-scroll).

---

## 9. Non-goals (YAGNI)

- No admin theme switch (already light via `ThemeService`).
- No rebuild of #5 (`AppDataGrid`) or #8 (Manufacturers) — already done.
- No semver/tagging scheme unless explicitly chosen (§5.2).
- No new admin pages or data; this is a consistency/chrome pass only.
- Not porting the mockup's inline-styled markup — recreate intent with MudBlazor + theme tokens.

---

## 10. Verification per PR

Per repo bar (`.claude/PR-AUDIT.md`): `/local-review` + `/standards-audit` (🔴 blocking),
CI-equivalent suite (`feedback_run_full_ci_suite_before_push`), `gh pr create` + `claude-code`
label, post-push code-scanning triage, and a green post-merge `Deploy`. Before/after screenshots of
each affected surface attached to the PR.
