---
title: "Admin per-source enable/disable toggle"
date: 2026-06-22
status: accepted
related:
  - docs/superpowers/specs/2026-06-22-admin-source-detail-design.md            # the #2 page this extends
  - docs/superpowers/specs/2026-06-22-admin-showcase-public-read-gated-write-design.md  # gated-mutation foundation (#477)
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md                # interactive-page doctrine (amendment)
  - docs/adr/0036-cosmos-read-access-standard.md                               # point-reads
  - docs/adr/0007-ingestion-sources-as-cosmos-data.md                          # IngestionSource is runtime config
  - docs/adr/0012-cosmos-arm-schema-data-plane-items.md                        # item CRUD via data-plane SDK
  - docs/adr/0008-mudblazor-strict.md
---

# Admin per-source enable/disable toggle

## 1. Problem & intent

The admin source-detail page (`/admin/sources/{id}`, feature #2) shows a source's
`Enabled` state as a read-only chip. An operator cannot pause or resume a source's
scheduled scraping without a redeploy or a manual Cosmos edit. This adds the
**first gated mutation** of the admin-capabilities roadmap (feature **#3**): an
admin-only toggle that flips `IngestionSource.Enabled`, reusing the public-read /
gated-write tiering and the `AdminActionGuard` server-side boundary established by
the showcase split (#477).

Disabling a source is **reversible and non-destructive**: it sets `Enabled = false`,
which the scraper orchestrator reads via `IIngestionSourceRepository.StreamEnabledAsync`
at the next scheduled run (a disabled source's ACA Job spins up and exits immediately,
per `IngestionSource.Enabled`'s contract). Re-enabling resumes at the next run. The
toggle has **no immediate scrape side effect** — it only persists the config flag.

## 2. Design

### 2.1 Surface & render mode

The toggle lives **only** on `AdminSourceDetail.razor`. Because the page now carries
an interactive control (`MudSwitch` with `@bind`/value-change), it changes from static
SSR to **`@rendermode InteractiveServer`** — the same move `AdminDocumentTriage`,
`AdminSettings`, `AdminMachineDetail`, and `AdminMachines` already made under the
ADR-0034 amendment (PR #424). `RenderModeConventionTests` *requires* the render-mode
attribute once an interactivity signal is present, so this is mandatory, not optional.

`[AllowAnonymous]` is **unchanged** — the page stays public-read; the *mutation* is
gated, not the page. The `AuthorizationContractTests.ShowcaseAdminPage_IsAllowAnonymous`
entry for `AdminSourceDetail` (added in #2) continues to hold.

The Sources grid (`AdminSources.razor`) and its name-link stay **static SSR, read-only**.

### 2.2 The control (Status row, config section)

The detail page's config section currently renders the `Enabled` state as a
`MudChip` (text "Enabled"/"Disabled" + colour). That becomes role-conditional:

- **Admin** (`_isAdmin == true`): a `MudSwitch<bool>` bound to the source's `Enabled`,
  with `Color="Color.Success"`, an `aria-label` of `"Toggle scraping for {DisplayName}"`,
  and a `data-testid="source-enabled-switch"`. While a write is in flight the switch is
  disabled and a `MudProgressCircular` (Size.Small) renders beside it
  (`data-testid="source-enabled-busy"`).
- **Anonymous / non-admin**: the existing read-only `MudChip` (text label + colour, not
  colour-only) — `data-testid="source-enabled-chip"`. A prospect sees the state but has
  no control.

The switch's `ValueChanged` invokes `ToggleEnabledAsync(bool newValue)` (not two-way
`@bind`, so the handler owns the state transition and can revert on failure).

### 2.3 Mutation flow

`ToggleEnabledAsync(bool newValue)` on the page:

1. **Server-side guard FIRST** — `if (!await Guard.IsAdminAsync(AuthState))` → warning
   snackbar `"Sign in as an administrator to perform this action."` and **return**
   (no repo call). UI hiding the switch is not the boundary (the #477 doctrine).
2. Set the busy flag, `StateHasChanged()`.
3. Call `SourceRepo.SetEnabledAsync(_source.Id, newValue, ct)` (30 s CTS).
4. On `true` (updated): set `_source.Enabled = newValue`; success snackbar
   `"{DisplayName} scraping {enabled|disabled}."`.
5. On `false` (source no longer exists in Cosmos): warning snackbar
   `"{DisplayName} no longer exists — refresh the page."`; **do not** change
   `_source.Enabled` (the switch reverts to its prior bound value).
6. On exception (incl. timeout): error snackbar; `_source.Enabled` unchanged (revert).
7. `finally`: clear the busy flag, `StateHasChanged()`.

The page injects `AdminActionGuard Guard`, `ISnackbar Snackbar`, and takes
`[CascadingParameter] Task<AuthenticationState>? AuthState`; `_isAdmin` is resolved once
in the load path via `Guard.IsAdminAsync(AuthState)` (UI gating), mirroring
`AdminDocumentTriage`.

**Load lifecycle.** Flipping to `InteractiveServer` adds a prerender pass, so the #2
load must move from `OnInitializedAsync` (which would run twice — prerender + circuit —
doubling the two Cosmos point-reads) to `OnAfterRenderAsync(bool firstRender)` with an
`if (!firstRender) return;` guard, matching the interactive siblings `AdminMachineDetail`
and `AdminDocumentTriage`. This loads once on the client and streams the shell + loading
bar instantly. The two reads and their per-section failure isolation (from #2) are
otherwise unchanged; `_isAdmin` is resolved in the same `LoadAsync`.

### 2.4 Persistence (Clean Architecture — mutation in Infrastructure)

New method on `IIngestionSourceRepository`:

```csharp
Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);
```

Implementation in `IngestionSourceRepository` mirrors the existing
`RecordRunResultAsync` read-modify-write shape (item CRUD via the data-plane SDK,
ADR-0012):

1. `GetByIdAsync(id, ConfigPartition, ct)` (single-partition point read, ADR-0036;
   `ConfigPartition = "config"`).
2. If null → `_logger.LogWarning(...)` (source not seeded / removed) and **return false**.
3. Else set `existing.Enabled = enabled`; `await UpsertAsync(existing, ct)`; **return true**.

The `bool` return is the honest-failure signal the UI uses to distinguish "updated"
from "source vanished" (Invariant #17) — never a silent no-op masquerading as success.

### 2.5 Error / honesty (Invariant #17)

- The server guard runs before any repo call; an unauthorized invoke makes **zero**
  Cosmos writes.
- A persistence failure (throw or `false`) never leaves the UI showing a state that
  didn't persist — the switch reverts to `_source.Enabled` and the snackbar names the
  failure. The success snackbar fires **only** after `SetEnabledAsync` returns `true`.
- Interactive page → `ISnackbar` is the feedback surface (a circuit exists, unlike the
  static #2 read paths which used `MudAlert`).

## 3. Components touched

- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor` —
  add `@rendermode InteractiveServer`; inject `AdminActionGuard` + `ISnackbar`; add
  `[CascadingParameter] AuthState` + `_isAdmin`; replace the Status chip with the
  role-conditional switch/chip; add `ToggleEnabledAsync`.
- Modify: `src/PinballWizard.Application/Persistence/IIngestionSourceRepository.cs` —
  add `SetEnabledAsync`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/IngestionSourceRepository.cs`
  — implement `SetEnabledAsync`.
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — stub
  `SetEnabledAsync` on the `IIngestionSourceRepository` double (return `true`) so the
  axe host renders the interactive page.
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourceDetailTests.cs` —
  add the gated-toggle scenarios (and update the existing render helper / context for
  the now-interactive page, mirroring `AdminDocumentTriageTests`).

## 4. Testing

Web bUnit (mirroring `AdminDocumentTriageTests`' anonymous-vs-authorized + mutation
pattern; authorized = `AddAuthorization().SetAuthorized(...).SetPolicies("AdminOnly")`,
anonymous = `AddAuthorization()` with no `SetAuthorized`):

- **Anonymous render**: `source-enabled-chip` present; `source-enabled-switch` **absent**.
- **Authorized render**: `source-enabled-switch` present and reflects `_source.Enabled`
  (on for an enabled source, off for a disabled one).
- **Toggle off**: triggering the switch's change (`<input>.Change(false)`, found inside
  `InvokeAsync` per the dispatcher-click rule) calls `SetEnabledAsync(id, false)` exactly
  once and the rendered state reflects disabled.
- **Toggle on**: from a disabled source, triggering the change calls
  `SetEnabledAsync(id, true)` exactly once.
- **Honest failure — source vanished**: `SetEnabledAsync` returns `false` →
  `_source.Enabled` unchanged (switch reverts; no state lie).
- **Honest failure — throw**: `SetEnabledAsync` throws → `_source.Enabled` unchanged
  (switch reverts), no unhandled exception.

**Server boundary coverage.** The handler calls `Guard.IsAdminAsync(AuthState)` at the
top before any repo call (the #477 doctrine). Two complementary tests cover the boundary,
matching the `AdminDocumentTriage` precedent: (a) the **anonymous render** test proves the
switch is absent for non-admins, so the mutation is unreachable from the UI; (b) the
existing `AdminActionGuardTests` prove the guard denies a non-admin principal. The
handler's top-of-method guard call is the server enforcement; it is not separately
bUnit-clicked because the guard is a concrete (non-substitutable) type and the anonymous
render already removes the only trigger — the same coverage shape the shipped triage
mutations use.
- `AuthorizationContractTests` still pins `AdminSourceDetail` as `[AllowAnonymous]`
  (unchanged from #2).
- `RenderModeConventionTests` passes with the new `@rendermode InteractiveServer`.
- axe stays clean on `/admin/sources/stern` (the switch carries its `aria-label`).

Persistence: the sibling `RecordRunResultAsync` (identical read-modify-write shape) has
no repo-level unit test today, so consumer-level (NSubstitute) coverage at the Web layer
matches the current project bar. Whether to add a Cosmos-emulator/Container-fake repo
test for `SetEnabledAsync` is resolved at plan-writing time; if added it lives in
`tests/PinballWizard.Infrastructure.Tests`.

## 5. Non-goals / YAGNI

- **Bulk enable/disable** of multiple sources — single-source toggle only.
- **ETag optimistic-concurrency conditional write** — single-admin showcase, last-write-
  wins via read-modify-write is acceptable; noted as a deferred enhancement.
- **Grid toggle** on `AdminSources` — stays static read-only (per the placement decision).
- **Immediate scrape trigger** — the flag is consumed by the orchestrator at the next
  scheduled run; toggling does not kick off a run.
- **Confirmation dialog** — the action is reversible; immediate + snackbar was chosen.

## 6. Risks

- **Render-mode flip of an existing page.** `AdminSourceDetail` was shipped static (#2);
  making it interactive means its bUnit tests must run under the interactive harness
  pattern (`AddAuthorization`, `MudPopoverProvider`, `InvokeAsync`) and the axe host must
  still render it clean. Mitigated by mirroring `AdminDocumentTriage`, which is the same
  shape (public-read page + gated actions + InteractiveServer).
- **State-revert correctness.** Using `ValueChanged` (not two-way `@bind`) so the handler
  owns the transition is essential: a failed write must not leave the switch visually
  "on" while Cosmos says "off". Covered by the two honest-failure tests.
- **Lost update under concurrent admins.** Last-write-wins (no ETag guard). Acceptable for
  a single-admin showcase; flagged as the deferred ETag enhancement.
</content>
