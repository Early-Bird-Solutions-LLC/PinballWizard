---
title: Admin test coverage — axe (a11y) + in-process real-circuit interactive tests
date: 2026-06-17
status: draft
issue: "#423"
related:
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md   # the render-mode work this verifies
  - docs/superpowers/specs/2026-06-17-admin-render-modes-design.md # the change under test
---

# Admin test coverage — axe (a11y) + in-process real-circuit interactive tests

## 1. Problem

The admin per-need render-mode work (ADR-0034 amendment, PR #424) made five `/admin/*`
pages interactive. That change is currently guarded by:

- **bUnit component tests** — but bUnit renders *everything* interactively
  (`UseInteractiveServerRendererInfo`), so it cannot prove a formerly-dead control now
  actually responds in a *real* Blazor Server circuit, nor that it was dead before.
- **`RenderModeConventionTests`** — a build-time static scan, not a runtime check.

Neither layer exercises a **real browser circuit**, which is the one thing that proves the
interactive admin controls (Settings `@bind` form, Triage `OnClick` actions, LinkOverrides
dialog, Machines/MachineDetail grids) function end-to-end. This is the documented PR #401
failure class — providers present but the circuit fails at runtime — which "the E2E canary
skip let ship." Separately, the existing `AccessibilityTests` axe suite covers only the
**public** routes; the now-interactive admin pages have no a11y scan, despite the
render-mode spec calling for them to stay axe-clean.

Issue #423 tracks both gaps.

### Constraints discovered during design

**(a) The SSR host serves no circuit.** The existing in-process Playwright host
(`PlaywrightWebApplicationFactory`) deliberately serves **SSR HTML only** — it does not serve
`blazor.web.js` / MudBlazor JS (the `MapStaticAssets` manifest "only exists in the published
Web project"), so there is **no interactive circuit** in that harness today.

**(b) Admin auth has a permissive no-tenant path — no test-auth handler needed.** `Program.cs`
branches on `AzureAd:TenantId` (lines 90-146): when a real tenant **is** configured it wires
OIDC (`AddMicrosoftIdentityWebApp`) and `AdminOnly = RequireRole("GlobalAdmin")`; when it is
**not** configured it registers `AddAuthentication()` with **no OIDC** and
`AdminOnly = RequireAssertion(_ => true)` — the documented local-dev posture. So a test host
that runs **without `AzureAd:TenantId`** has no OIDC middleware to challenge the circuit and an
`AdminOnly` policy that passes for an anonymous request. Admin pages render without any
test-auth handler. (The existing minimal SSR factory can't render admin pages today only
because it registers `AddAuthorization()` with **no `AdminOnly` policy** and none of the admin
pages' injected services — both fixed below.) This also retires the deployed-canary's
standing-admin-account problem: the in-process route never needs a real `GlobalAdmin`.

A deployed-canary approach (extend `canary.yml`) was rejected during design: the canary
points `E2E__BaseUrl` at the ACA FQDN (bypassing Cloudflare anyway), the existing public
canary already proves the InteractiveServer circuit mechanism on the same hosting stack, and
authenticating as `GlobalAdmin` in a headless cron would require a **standing, no-MFA,
password-auth GlobalAdmin test account** — a security-posture liability on the personal
tenant. The deterministic in-process route below avoids all of that.

## 2. Goal & success criteria

- **Half A:** every routable admin page has a WCAG 2.1 AA axe scan (SSR HTML) that fails the
  build on a violation — mirroring the existing public `AccessibilityTests`.
- **Half B:** a deterministic in-process harness runs a **real Blazor Server circuit** for
  authenticated-admin pages, and Playwright proves an interactive control responds on **every**
  interactive admin page (the per-control-type primitives — `OnClick`, `@bind`, dialog, grid
  sort/group — all exercised).
- **No external dependencies:** no Azure, Entra, Cloudflare, or standing admin account. Both
  halves run from a clean checkout.
- **#423 fully closed** by the in-process route.

## 3. Shared foundation

Both halves render admin pages (which carry `[Authorize(Policy = "AdminOnly")]`) and need the
admin pages' injected services satisfied.

### 3.1 Satisfy `AdminOnly` via the permissive no-tenant policy (no test-auth handler)

Admin pages render in a test host that registers the **permissive `AdminOnly` policy** exactly
as `Program.cs`'s no-tenant branch does:

```csharp
services.AddAuthorization(o => o.AddPolicy("AdminOnly", p => p.RequireAssertion(_ => true)));
```

This is the real local-dev posture (no `AzureAd:TenantId` ⇒ no OIDC, permissive `AdminOnly`),
so it is faithful to how the app actually runs without a tenant — not a test-only fiction. No
`TestAuthHandler` change is required: the existing anonymous `NoResult` identity passes a
`RequireAssertion(_ => true)` policy. The existing public anonymous axe suite is untouched (it
uses the factory's existing `AddAuthorization()` with no `AdminOnly` policy; admin tests use a
factory that adds the permissive policy).

### 3.2 `AdminTestDoubles` — one registration extension + seed fixture

A single `IServiceCollection` extension (`AddAdminTestDoubles`) registers in-memory stubs for
every service the six admin pages inject, returning a small, realistic fixture defined once and
reused by both factories:

| Service | Stub returns |
|---|---|
| `ICatalogStatsReadRepository` | 2 manufacturers; an edition family with one 0-doc sibling (so health chips + edition-gap render) |
| `IMachineRepository` | the seed machines (point read by OpdbId+mfr; siblings by GroupId) |
| `IMachineDocumentReadRepository` | a couple linked-document rows for the detail grid |
| `IRawDocumentRepository` | 1–2 triage rows (`Failed`/`PlatformGeneric`); `UpdateLinkStatusAsync` no-ops; `GetAsync` returns the row |
| `IDocumentLinker` | `LinkAsync` returns `Linked` (so a Relink resolves the row) |
| `ILinkOverrideRepository` | one override row; `UpsertAsync`/`DeleteAsync` mutate the in-memory set |
| `IAdminSettingsRepository` | a couple stored rows; `SetAsync`/`DeleteAsync` mutate in-memory |
| `IAgentPromptOverrideRepository` | empty version list / no active override |
| `EmbeddedResourceAgentPromptProvider` | the real type resolves its embedded prompts (no stub needed) or a thin stub returning a fixed string |
| `IOptions<AiFoundryOptions>` | a default options instance (so `WellKnownSettings.DefaultFor` resolves) |

The fixture data is deliberately small and synthetic (no real PII; no real machine catalog).
A stub may be configured per-test to **throw** (to exercise the load-failure alert path in axe
or circuit tests) — a simple toggle on the doubles registration.

## 4. Half A — admin axe (a11y)

An admin variant of the SSR factory: same minimal Kestrel + SSR-only host as
`PlaywrightWebApplicationFactory`, but registering the **permissive `AdminOnly` policy** (§3.1)
+ `AddAdminTestDoubles` + the `EmbeddedResourceAgentPromptProvider` singleton. Implementation
may parameterize the existing factory (preferred — one factory, a constructor flag) or add a
focused subclass; the plan decides based on the cleanest diff.

A `Theory` covers every routable admin route, asserting **zero WCAG 2.1 AA violations** on the
SSR HTML (axe on `DOMContentLoaded`, identical to the public test):

- `/admin` (Dashboard)
- `/admin/sources`
- `/admin/machines`
- `/admin/machines/{opdbId}?mfr={mfr}` (using a seed machine id + manufacturer)
- `/admin/document-triage`
- `/admin/link-overrides`
- `/admin/settings`

This is SSR axe (pre-JS), consistent with the established pattern — it validates the markup
screen-readers and crawlers encounter first. (Post-render axe inside the Half-B circuit host is
a possible bonus but is **out of scope** — YAGNI.)

## 5. Half B — in-process real-circuit interactive tests

### 5.1 Walking skeleton (task 1 — the de-risk)

Stand up a Kestrel-hosted host (`InteractiveAdminWebApplicationFactory`) that:

1. **Runs the real Web app** (`Program.cs`) **with `AzureAd:TenantId` unset** → the no-tenant
   branch: **no OIDC** (so nothing challenges the `/_blazor` circuit) and a permissive
   `AdminOnly`. This is the key simplification — the OIDC-override problem the SSR factory
   documented only exists when a tenant is configured; with none, the real app serves admin
   pages and its **real `MapStaticAssets()`** delivers `blazor.web.js` + MudBlazor JS, so the
   browser establishes a live circuit.
2. **Replaces the admin pages' backend services with `AddAdminTestDoubles`** (the Web host has
   no Foundry/Cosmos; the real repos would fail to resolve or hang) via the host's test-service
   override hook.
3. **Exposes a real TCP address on Kestrel** (loopback, port 0) so Playwright can connect —
   `WebApplicationFactory`'s in-memory `TestServer` is not reachable by a real browser, so the
   host must bind Kestrel and surface the address (the existing `PlaywrightWebApplicationFactory`
   already demonstrates the Kestrel-on-loopback pattern).

The skeleton is **proven** by one decisive interaction in a real browser: load
`/admin/machines`, click a group-by axis button, assert the grid regroups / the active button
flips **without a navigation** (pure in-circuit client-side state). If this cannot be made to
work at acceptable cost, **stop and reassess** (fallback in §7) before building the rest of
Half B — static-asset manifest discovery in the test host is now the single riskiest
assumption (auth is no longer a risk per §3.1 / the no-tenant branch).

- **Approach (recommended):** run the real `Program.cs` app (real `MapStaticAssets`) with
  `AzureAd:TenantId` unset + `AddAdminTestDoubles` overrides, bound to a real Kestrel loopback
  port for Playwright. The static-asset manifest (`PinballWizard.Web.staticwebassets.endpoints.json`)
  is produced by building the Web project and is discovered from its content root — so the host
  sets its content root to the Web project so `MapStaticAssets` finds the manifest.
- **Fallback:** if in-process manifest discovery proves intractable, **spawn the real published
  Web app out-of-process** (the `tools/e2e/Run-E2E.ps1` spawn pattern) with `AzureAd__TenantId`
  unset and the admin backends stubbed via configuration, and point Playwright at its URL. Still
  deterministic and local (no Azure/Entra/Cloudflare). The skeleton task picks whichever works.

### 5.2 Per-page interactive tests (broad coverage)

Once the skeleton lives, one test per interactive admin page exercises its interactivity
primitive on the real circuit:

| Page | Interaction | Assertion |
|---|---|---|
| AdminMachines | click a group-by axis button | grid regroups; active button gets `mud-button-filled-primary`; no navigation |
| AdminSettings | drag/change a `@bind` slider or numeric | bound value text updates; dirty-state hint appears |
| AdminLinkOverrides | click "New Override" | the `MudDialog` opens (dialog content visible) |
| AdminDocumentTriage | click "Re-link" on a row | row resolves and leaves the grid (stub linker returns `Linked`) + success snackbar |
| AdminMachineDetail | click a sortable docs-grid column header | rows reorder (sort applied client-side) |

Each test reuses the **circuit-lag retry pattern** from `WizardE2ETests` (the circuit can trail
the prerender; clicks retry against a re-resolved locator, with a bounded timeout).

### 5.3 Where these live

New test class(es) under `tests/PinballWizard.Web.Tests/A11y/` or a new
`tests/PinballWizard.Web.Tests/Circuit/` folder (the plan decides; Circuit/ keeps the
interactive-circuit harness distinct from the SSR axe harness). Not gated by
`E2EFactAttribute` — these are deterministic and need no live stack.

## 6. CI & determinism

Both halves are deterministic and Azure-free, so they run in CI's existing **"UI tests
(axe-core + responsive snapshots, Playwright)"** job, with an added step to **publish the Web
project** before the run (Half B needs the static-asset output). Half A needs no publish (SSR
host builds its own minimal app).

Fallback if Half B's publish+circuit proves too heavy/slow for PR CI: keep **Half A in PR CI**
and gate **Half B** behind a trait (run locally + in the scheduled job) — but the design target
is both in PR CI.

## 7. Risks

- **Static-asset manifest discovery in a test host (Half B) is the real unknown** (auth is
  not, per §3.1). Mitigated by the skeleton-first task: if the recommended in-process approach
  and the out-of-process-spawn fallback (§5.1) both prove too costly, Half B degrades to a
  **documented manual admin smoke step** in the deploy runbook (you, with a real admin login,
  click one control post-deploy) while Half A still ships — and #423 is updated to reflect the
  partial automated closure. This fallback is only taken if the skeleton genuinely can't be made
  to work.
- **Playwright circuit-lag flakiness** → reuse the existing bounded-retry pattern; no fixed
  sleeps.
- **Stub-repo breadth** (6 pages × several services) → contained in the single
  `AddAdminTestDoubles` extension; the skeleton surfaces any larger-than-expected DI surface
  early.

## 8. Non-goals / YAGNI

- No Cloudflare / Entra / deployed-canary path; no standing admin account; no per-user data.
- No post-render axe inside the circuit host (SSR axe is the a11y contract).
- No new admin features or page changes — this effort is test coverage only.
- Not exercising every control on every page — one decisive interaction per page (the
  primitives), not exhaustive control enumeration.
