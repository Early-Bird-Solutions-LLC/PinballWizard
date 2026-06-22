---
title: "Admin showcase: public-read / gated-write split (foundation)"
date: 2026-06-22
status: accepted
related:
  - docs/adr/0009-entra-external-id-admin-rbac-v1.md       # AdminOnly role gate
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md
  - docs/adr/0008-mudblazor-strict.md
  - tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs   # rewritten by this work
---

# Admin showcase: public-read / gated-write split (foundation)

## 1. Problem & intent

The admin area (`/admin/*`) is a deliberately enterprise-grade operations surface — and
for a customer-facing showcase it is wasted behind a login. The intent is to make the
**read-only, non-sensitive** admin surfaces **part of the public showcase** ("here is the
real operations console, running live"), while keeping every **mutation** and every
**sensitive** surface gated behind the existing Entra admin role.

Today every `/admin/*` page carries page-level `[Authorize(Policy="AdminOnly")]`, and an
assembly-scan contract test (`AuthorizationContractTests`) actively *forbids* any admin
page from being public — exactly the boundary this work re-draws. Re-drawing a security
boundary on a showcase app is the riskiest kind of change, so this foundation is its own
spec/PR, separate from the feature capabilities that build on it.

This is **sub-project #1 of 5**. The follow-ons (each its own later spec): #2 Source-detail
page · #3 Per-source enable/disable · #4 Corpus/RAG stats panel · #5 Scrape-run history
timeline. They all classify into the tiering this foundation establishes.

## 2. Current auth model (verified)

- **No `FallbackPolicy`** (`Program.cs:102`) — removed 2026-06-12 because it challenged the
  anonymous Blazor negotiate and killed public circuits (PR #389). Consequence: a routable
  page with **no** auth attribute is **public by default**; public pages opt in explicitly
  with `[AllowAnonymous]`, and the contract test pins that.
- **`AdminOnly` policy:** auth-configured (prod) path → `RequireRole("GlobalAdmin")`
  (`Program.cs:119`). No-tenant local-dev path → `RequireAssertion(_ => true)`
  (`Program.cs:144`, permissive so local dev is fully functional).
- `AddCascadingAuthenticationState()` is wired (`Program.cs:126`) — `AuthorizeView` and the
  cascading `AuthenticationState` work. (Plan must confirm it is present on **both** the
  auth-configured and no-tenant branches.)
- Pages are a mix of static SSR (Dashboard, Sources) and `InteractiveServer` (Machines,
  MachineDetail, DocumentTriage, LinkOverrides, Settings). `AuthorizeView` works in both.

## 3. Design

### 3.1 Per-control gating model

Each public-read admin page changes from page-level `[Authorize(Policy="AdminOnly")]` to
page-level **`[AllowAnonymous]`**, and gates *within* the page:

- Mutation controls and sensitive/identity content wrap in
  `<AuthorizeView Policy="AdminOnly">` — `Authorized` renders the control; `NotAuthorized`
  renders nothing (or a read-only hint where one helps).
- **Server-side authorization on every mutation handler (mandatory — the real boundary).**
  `AuthorizeView` only governs rendering. Each gated handler
  (`AdminSettings.SaveAsync`/`ResetAsync`/prompt save/activate/revert,
  `AdminDocumentTriage.RelinkAsync`/`MarkGenericAsync`,
  `AdminLinkOverrides` create/delete) first calls a shared guard that evaluates the
  `AdminOnly` policy against the current user via `IAuthorizationService`; if it fails, the
  handler refuses (no-op + a non-sensitive notice) and **does not** touch the repository.
  UI hiding is never the boundary. The guard is one shared helper (a small
  `AdminActionGuard` injected service, or a base-component method) so the pattern can't
  drift across pages.

### 3.2 Sensitivity tiering (the spine)

| Surface | Anonymous (public) sees | Gated behind `AdminOnly` |
|---|---|---|
| Dashboard | all counts | — (no mutations) |
| Machines, MachineDetail | full catalog + health + linked docs | — (no mutations) |
| Sources | full grid (name/URL/cadence/status/run stats) | — (toggle is feature #3) |
| Document Triage | the queue (read): doc URLs, types, statuses, failure reasons | Relink / MarkGeneric buttons + busy state |
| Link Overrides | the override rows (pattern → machine IDs) | create dialog + delete; **`createdBy` + `notes` identity redacted** |
| Settings | the live values + defaults (Guardrails incl. cost ceiling, Conversation, RAG Retrieval), read-only | sliders/numeric edit, Save, Reset, **provenance lines** ("overridden by … on …"), and the **entire Prompt Templates tab** |

**Identity/provenance redaction (cross-cutting):** any "by `<name>` / `<email>` on `<date>`"
content — Settings `ProvenanceLine`, Link-Override `createdBy`/`notes`, prompt-version
authors — is wrapped so anonymous viewers never see operator identity. The underlying *data*
(the value, the override target) still shows; only the *who* is gated.

**Prompt Templates** (`MudTabPanel`) wraps in `AuthorizeView` so the tab itself does not
render for anonymous users — the agent prompt text is IP and never reaches the public.

### 3.3 Showcase surfacing

- **Public nav entry** in `MainLayout` header: **"Behind the Scenes"** → `/admin`. Visible
  to everyone, framed as an intentional showcase of the ops console.
- **Persistent admin drawer** (`AdminLayout`) stays visible to all visitors (already
  persistent). The drawer lists the read surfaces for everyone; it does not advertise the
  Prompt Templates tab (that lives inside Settings and is gated there).
- **Read-only banner + sign-in affordance:** when anonymous, `AdminLayout` shows a banner
  — *"Read-only view · Sign in to manage"* — with a sign-in link routing to the existing
  OIDC challenge (exact route — e.g. `MicrosoftIdentity/Account/SignIn` vs a custom
  `/authentication/login` — verified at plan time). When authorized, the banner and link
  are replaced by the operator identity / sign-out (via `AuthorizeView`).

### 3.4 No Cloudflare change

The only Cloudflare Access rule is the **whole-site pre-launch gate** (`access.tf`, apex +
www → maintainer email), which is orthogonal to this work and stays as-is. `waf.tf` blocks
only scanner paths (`/wp-admin`, `/phpmyadmin`) — nothing touches `/admin`. This foundation
is a pure app-level auth re-tiering; **no infra/tofu change**.

## 4. The safety net (most security-critical deliverable)

Re-drawing the boundary means the tests that *pin* the boundary must change in lockstep, or
a future edit silently re-exposes something. Two layers:

### 4.1 Rewritten `AuthorizationContractTests`

The current contract ("every routable admin component MUST carry `AdminOnly`; none may be
public") inverts to an **explicit-classification** contract:

- Every routable component in the Admin pages namespace MUST carry **exactly one** of
  `[AllowAnonymous]` or `[Authorize(Policy="AdminOnly")]` — **never neither** (with no
  FallbackPolicy, an un-attributed admin page is accidentally public; the test makes the
  classification a conscious, reviewed decision).
- The pages this design makes public (`AdminDashboard`, `AdminSources`, `AdminMachines`,
  `AdminMachineDetail`, `AdminDocumentTriage`, `AdminLinkOverrides`, `AdminSettings`) are
  pinned to `[AllowAnonymous]` by an explicit list, so removing it (re-gating a page
  wholesale, or fat-fingering the attribute) fails the build.
- Public non-admin pages keep their existing `[AllowAnonymous]` assertions.

Reflection cannot see in-component `AuthorizeView`, so the contract test guarantees only the
*page-level* classification. The per-control boundary is proven by 4.2.

### 4.2 bUnit anonymous-vs-authorized render tests (load-bearing)

For each page with gated content (DocumentTriage, LinkOverrides, Settings), two render
tests using the existing bUnit auth doubles (`AddAuthorization().SetAuthorized(...)` vs
not-authorized):

- **Anonymous render asserts:** the read data IS present (grid rows / setting values), and
  the gated `data-testid`s are **absent** — no mutation buttons, no Settings edit controls,
  **no Prompt Templates tab**, no identity/provenance elements.
- **Authorized render asserts:** the same gated `data-testid`s ARE present.

Server-side guard (3.1) gets a focused unit test: the shared `AdminActionGuard` denies for
an unauthenticated/role-less principal and allows for a `GlobalAdmin` principal — so the
defense-in-depth layer is proven independently of the UI.

The existing `AdminAccessibilityTests` (Playwright/axe) and
`InteractiveAdminWebApplicationFactory` run on the permissive no-tenant path; they keep
working (anonymous there still passes the permissive `AdminOnly`). Axe must stay clean on
the public read render.

## 5. Components touched

- `src/PinballWizard.Web/Components/Pages/Admin/*.razor` — swap page-level attribute to
  `[AllowAnonymous]`; wrap mutations / identity / Prompt Templates tab in `AuthorizeView`;
  add server-side guard calls in mutation handlers.
- `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` — read-only banner + sign-in
  affordance (`AuthorizeView`).
- `src/PinballWizard.Web/Components/Layout/MainLayout.razor` (or the public header nav
  component) — "Behind the Scenes" entry.
- New shared `AdminActionGuard` (Web layer) — wraps `IAuthorizationService` + cascading
  `AuthenticationState`; one `EnsureAdminAsync()` method returning allow/deny.
- `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs` — rewritten (4.1).
- `tests/PinballWizard.Web.Tests/Components/Admin/*Tests.cs` — anonymous/authorized render
  tests (4.2); a guard unit test.
- Stale-comment cleanup in the admin pages whose header comments still cite the old
  "FallbackPolicy / AdminOnly per-page" model (Dashboard/Sources headers reference it).

## 6. Testing

- Rewritten contract tests (4.1) — fail the build if an admin page lacks an explicit
  classification or a public-pinned page loses `[AllowAnonymous]`.
- bUnit anonymous-vs-authorized render tests for DocumentTriage, LinkOverrides, Settings
  (4.2) — the real per-control boundary proof.
- `AdminActionGuard` unit test — allow/deny by role.
- Existing admin bUnit + axe suites stay green (permissive-path harness unaffected).
- Full `dotnet build -warnaserror` clean; `/standards-audit` + `/local-review` pre-push.

## 7. Risks

- **Accidental over-exposure** is the headline risk. Mitigations: explicit-classification
  contract test (4.1), anonymous-render tests asserting *absence* of every gated element
  (4.2), and the server-side guard (3.1) so even a forged circuit event can't mutate.
- **Provenance leak through a missed control** — the redaction is per-control, so a new
  identity-bearing element could be forgotten. Mitigation: the anonymous-render tests
  assert identity `data-testid`s are absent; `/local-review` cat-8 covers the rest.
- **Local-dev permissive policy** means `AuthorizeView Policy="AdminOnly"` shows controls to
  everyone locally (RequireAssertion(true)). This is intended (local dev is fully
  functional) and does not affect prod, where `RequireRole("GlobalAdmin")` is the gate. The
  bUnit tests set authorization explicitly, so they prove both states regardless.

## 8. Non-goals / YAGNI

- The four feature capabilities (#2 source-detail, #3 enable/disable, #4 corpus stats,
  #5 run-history) — separate specs.
- Any Cloudflare / pre-launch-gate change.
- Any new persistence or repository.
- Changing the `AdminOnly` role definition or the Entra app-role model (ADR-0009 stands).
