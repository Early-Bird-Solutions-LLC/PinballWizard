# Admin Showcase: public-read / gated-write split — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the read-only, non-sensitive admin surfaces publicly viewable as part of the showcase, while keeping every mutation and every sensitive surface gated behind the existing Entra `AdminOnly` role — enforced both in the UI and server-side.

**Architecture:** Each public-read admin page flips from page-level `[Authorize(Policy="AdminOnly")]` to page-level `[AllowAnonymous]`, and gates *within* the page: a `_isAdmin` flag (resolved once via a new `AdminActionGuard` against the `AdminOnly` policy) conditionally renders mutation controls, identity/provenance, and the Prompt Templates tab; every mutation handler additionally calls a server-side guard before touching a repository (UI hiding is never the boundary). A rewritten `AuthorizationContractTests` (explicit-classification contract) plus bUnit anonymous-vs-authorized render tests are the safety net.

**Tech Stack:** Blazor (.NET 10) static SSR + InteractiveServer, MudBlazor 8.x, `Microsoft.AspNetCore.Authorization` / `IAuthorizationService`, Microsoft.Identity.Web (sign-in UI), bUnit + NSubstitute + xUnit.

## Global Constraints

- **Explicit classification:** every routable component in `PinballWizard.Web.Components.Pages.Admin` must carry **exactly one** of `[AllowAnonymous]` or `[Authorize(Policy="AdminOnly")]` — never neither (no FallbackPolicy → un-attributed = accidentally public), never both. Pinned by the rewritten contract test.
- **Server-side guard is the boundary:** every gated mutation handler calls `AdminActionGuard` and refuses (no-op) if not authorized, BEFORE any repository call. `_isAdmin`/`AuthorizeView` only governs rendering.
- **Sensitivity tiering:** public-read = Dashboard counts, Machines catalog/detail, Sources list, Settings *values*, Document-Triage queue, Link-Override rows (pattern + machine IDs). Gated = all mutations, **operator identity/provenance** (Settings provenance lines, Link-Override `CreatedBy`/`Notes`, prompt authors), and the **entire Prompt Templates tab**.
- **`AdminOnly` policy:** prod = `RequireRole("GlobalAdmin")`; no-tenant local-dev = `RequireAssertion(_ => true)` (permissive — local dev stays fully functional, so `_isAdmin` is true locally). Tests set authorization explicitly.
- **Sign-in route:** `/MicrosoftIdentity/Account/SignIn` (sign-out `/MicrosoftIdentity/Account/SignOut`), provided by `AddMicrosoftIdentityUI()` on the auth-configured path.
- **No Cloudflare/infra change.** No new persistence. MudBlazor strict (ADR-0008); no hex colours, `Color.*` only. Identity: commits authored `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`, conventional subject, no Claude attribution trailer.
- **Test command:** `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`.

---

### Task 1: `AdminActionGuard` service

The shared server-side authorization primitive. Wraps `IAuthorizationService` + the `AdminOnly` policy; resolves the current user from the cascading `AuthenticationState`.

**Files:**
- Create: `src/PinballWizard.Web/Security/AdminActionGuard.cs`
- Modify: `src/PinballWizard.Web/Program.cs` (register the service, unconditional)
- Test: `tests/PinballWizard.Web.Tests/Security/AdminActionGuardTests.cs`

**Interfaces:**
- Produces: `PinballWizard.Web.Security.AdminActionGuard` with
  `Task<bool> IsAdminAsync(System.Security.Claims.ClaimsPrincipal user)` and
  `Task<bool> IsAdminAsync(Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? authState)`. Both return true iff the user satisfies the `AdminOnly` policy. A null `authState` resolves to an anonymous principal (false under `RequireRole`).

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Web.Tests/Security/AdminActionGuardTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Security;

// Unit tests for AdminActionGuard — the server-side authorization boundary for
// admin mutations. Exercises the real AuthorizationService with the production
// AdminOnly policy (RequireRole("GlobalAdmin")) so allow/deny is proven against
// the actual policy, not a mock.
public sealed class AdminActionGuardTests
{
    private static AdminActionGuard BuildGuard()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(o =>
            o.AddPolicy("AdminOnly", p => p.RequireRole("GlobalAdmin")));
        services.AddLogging();
        var authz = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        return new AdminActionGuard(authz);
    }

    [Fact]
    public async Task IsAdminAsync_GlobalAdminPrincipal_ReturnsTrue()
    {
        var guard = BuildGuard();
        var admin = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "GlobalAdmin")], "test"));

        Assert.True(await guard.IsAdminAsync(admin));
    }

    [Fact]
    public async Task IsAdminAsync_AnonymousPrincipal_ReturnsFalse()
    {
        var guard = BuildGuard();
        var anon = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(await guard.IsAdminAsync(anon));
    }

    [Fact]
    public async Task IsAdminAsync_NonAdminAuthenticatedPrincipal_ReturnsFalse()
    {
        var guard = BuildGuard();
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "joe")], "test"));

        Assert.False(await guard.IsAdminAsync(user));
    }

    [Fact]
    public async Task IsAdminAsync_NullAuthState_ReturnsFalse()
    {
        var guard = BuildGuard();

        Assert.False(await guard.IsAdminAsync((Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>?)null));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminActionGuardTests"`
Expected: FAIL to compile — `AdminActionGuard` does not exist.

- [ ] **Step 3: Create the service**

Create `src/PinballWizard.Web/Security/AdminActionGuard.cs` (use `//` comments — this project forbids `///` XML docs):

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace PinballWizard.Web.Security;

// Server-side authorization boundary for admin mutations. Admin pages are
// publicly viewable ([AllowAnonymous]) with read-only content; every mutation
// handler MUST call this guard before acting, because AuthorizeView / _isAdmin
// only govern rendering — they are not a security boundary. Resolves the
// AdminOnly policy (prod: RequireRole("GlobalAdmin")) against the current user.
public sealed class AdminActionGuard(IAuthorizationService authorizationService)
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public async Task<bool> IsAdminAsync(ClaimsPrincipal user) =>
        (await authorizationService.AuthorizeAsync(user, "AdminOnly")).Succeeded;

    public async Task<bool> IsAdminAsync(Task<AuthenticationState>? authState)
    {
        var user = authState is null ? Anonymous : (await authState).User;
        return await IsAdminAsync(user);
    }
}
```

- [ ] **Step 4: Register the service in `Program.cs`**

In `src/PinballWizard.Web/Program.cs`, after the `if (isAuthConfigured) { … } else { … }` auth block (both branches register `IAuthorizationService`), add an unconditional registration. Place it right after the closing `}` of the `else` branch (near line 147):

```csharp
// Server-side authorization guard for admin mutation handlers (AdminActionGuard).
// Registered on both auth paths — admin pages are public-read with gated
// mutations, and the handlers call this before touching a repository.
builder.Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminActionGuardTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Security/AdminActionGuard.cs src/PinballWizard.Web/Program.cs tests/PinballWizard.Web.Tests/Security/AdminActionGuardTests.cs
git commit -m "feat(web) add AdminActionGuard server-side admin authorization guard" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 2: Public read-only pages (no mutations) + rewritten contract test

Flip the four no-mutation admin pages to `[AllowAnonymous]`, rewrite `AuthorizationContractTests` to the explicit-classification contract, and fix the stale auth header comments. These pages have no mutations and no operator identity, so flipping them is safe with no per-control gating.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor`, `AdminSources.razor`, `AdminMachines.razor`, `AdminMachineDetail.razor`
- Modify (rewrite): `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs`

**Interfaces:**
- Produces: the contract-test helper list `ShowcaseAdminPage_IsAllowAnonymous` (a `[Theory]` whose `[InlineData]` set grows in Tasks 3–5 as more pages flip) and the stable `EveryRoutableAdminComponent_HasExactlyOneExplicitClassification` invariant.

- [ ] **Step 1: Rewrite the contract test (write the new contract first)**

Replace the entire contents of `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs` with:

```csharp
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using PinballWizard.Web.Components.Pages;
using IndexPage = PinballWizard.Web.Components.Pages.Index;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Security;

// Authorization contract tests — pin the route-level authorization structure
// after the admin showcase split (2026-06-22).
//
// Model: there is NO FallbackPolicy (Program.cs) — a routable page with no auth
// attribute is PUBLIC by default. The admin area is now a public-read showcase:
// pages carry [AllowAnonymous] and gate mutations / sensitive content per-control
// (proven by bUnit anonymous-vs-authorized render tests) plus a server-side
// AdminActionGuard. Fully-gated admin pages (if any) carry
// [Authorize(Policy="AdminOnly")]. Every admin page MUST be EXPLICITLY one or the
// other — never neither (accidental exposure), never both.
public sealed class AuthorizationContractTests
{
    // ── Every routable admin component carries exactly ONE explicit classification ──
    [Fact]
    public void EveryRoutableAdminComponent_HasExactlyOneExplicitClassification()
    {
        var adminNamespace = typeof(AdminDashboard).Namespace!;
        var offenders = typeof(AdminDashboard).Assembly.GetTypes()
            .Where(t => t.Namespace == adminNamespace)
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .Select(t => new
            {
                t.Name,
                Anon = t.GetCustomAttribute<AllowAnonymousAttribute>() is not null,
                Admin = t.GetCustomAttribute<AuthorizeAttribute>() is { Policy: "AdminOnly" },
            })
            .Where(x => x.Anon == x.Admin) // neither (both false) or both (both true)
            .Select(x => x.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Routable admin component(s) lacking exactly one explicit auth classification " +
            "([AllowAnonymous] XOR [Authorize(Policy=\"AdminOnly\")]). With no FallbackPolicy, " +
            "neither = accidentally PUBLIC: " + string.Join(", ", offenders));
    }

    // ── Showcase admin pages are public-read ([AllowAnonymous]) ────────────────
    // These pages render read-only content to everyone and gate mutations /
    // sensitive content per-control (bUnit render tests) + AdminActionGuard.
    // Removing [AllowAnonymous] (re-gating wholesale) fails here.
    [Theory]
    [InlineData(typeof(AdminDashboard))]
    [InlineData(typeof(AdminSources))]
    [InlineData(typeof(AdminMachines))]
    [InlineData(typeof(AdminMachineDetail))]
    public void ShowcaseAdminPage_IsAllowAnonymous(Type page)
    {
        Assert.NotNull(page.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(page.GetCustomAttribute<AuthorizeAttribute>());
    }

    // ── Public non-admin pages MUST carry [AllowAnonymous] ─────────────────────
    [Theory]
    [InlineData(typeof(IndexPage))]
    [InlineData(typeof(Wizard))]
    [InlineData(typeof(About))]
    [InlineData(typeof(Settings))]
    [InlineData(typeof(Status))]
    [InlineData(typeof(Error))]
    [InlineData(typeof(NotFound))]
    public void PublicPage_HasAllowAnonymous(Type page)
    {
        Assert.NotNull(page.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
```

- [ ] **Step 2: Run the contract test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AuthorizationContractTests"`
Expected: FAIL — `EveryRoutableAdminComponent_HasExactlyOneExplicitClassification` fails (the 4 pages still carry `[Authorize(AdminOnly)]`, which is fine, but `AdminDocumentTriage`/`AdminLinkOverrides`/`AdminSettings` also still carry it — they're `Admin`=true, `Anon`=false → exactly one → OK) and `ShowcaseAdminPage_IsAllowAnonymous` fails for the 4 pages (still `[Authorize]`, not `[AllowAnonymous]`).

- [ ] **Step 3: Flip the four no-mutation pages to `[AllowAnonymous]`**

In each of the four files, replace the auth attribute line.

`AdminDashboard.razor` line 4: replace
```razor
@attribute [Authorize(Policy = "AdminOnly")]
```
with
```razor
@attribute [AllowAnonymous]
```
Also fix the stale header comment in `AdminDashboard.razor` (lines 16-18) — replace the "Auth: protected by the global FallbackPolicy …" block with:
```razor
 * Auth: public-read showcase surface ([AllowAnonymous]). Read-only counts; no
 * mutations. There is no FallbackPolicy — public-read admin pages opt in with
 * [AllowAnonymous] and gate any mutations per-control + via AdminActionGuard.
```

`AdminSources.razor` line 4: same attribute swap. Fix its stale header comment (line 19) "Auth: protected by the global FallbackPolicy — see AdminDashboard.razor." → 
```razor
 * Auth: public-read showcase surface ([AllowAnonymous]). Read-only grid; no mutations.
```

`AdminMachines.razor` and `AdminMachineDetail.razor`: swap the `@attribute [Authorize(Policy = "AdminOnly")]` line to `@attribute [AllowAnonymous]`. (Their `@using Microsoft.AspNetCore.Authorization` stays — `AllowAnonymousAttribute` is in that namespace.)

Confirm each file still has `@using Microsoft.AspNetCore.Authorization` (it does — it provided `AuthorizeAttribute`; it also provides `AllowAnonymousAttribute`).

- [ ] **Step 4: Run the contract + affected page tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AuthorizationContractTests|FullyQualifiedName~AdminDashboardTests|FullyQualifiedName~AdminSourcesTests|FullyQualifiedName~AdminMachinesTests|FullyQualifiedName~AdminMachineDetailTests"`
Expected: PASS. (`EveryRoutableAdminComponent_HasExactlyOneExplicitClassification` passes — all 7 admin pages have exactly one classification, the 4 flipped + 3 still `AdminOnly`. `ShowcaseAdminPage_IsAllowAnonymous` passes for the 4. Existing page render tests still pass — they SetAuthorized, which still renders the read content.)

- [ ] **Step 5: Add an anonymous-render smoke for the flipped pages**

Append to `tests/PinballWizard.Web.Tests/Components/Admin/AdminDashboardTests.cs` a test that renders WITHOUT authorization and asserts the read content still shows (the page is now public). Add inside the class:

```csharp
[Fact]
public void Anonymous_RendersCounts_PagePublic()
{
    RegisterAll(StatsStream, sourceCount: 2, overrideCount: 1);
    // No AddAuthorization().SetAuthorized — anonymous viewer.
    this.AddAuthorization(); // registers the auth context as NOT authorized
    _ = Services.GetRequiredService<BunitNavigationManager>();

    var cut = Render<AdminDashboard>();

    cut.WaitForAssertion(() =>
        Assert.Equal("3", cut.Find("[data-testid='admin-machines-count']").TextContent.Trim()));
}
```

(The existing `AdminDashboardTests` constructor already calls `this.AddAuthorization().SetAuthorized(...)`; for this anonymous test, override by constructing a fresh context is not possible per-test — instead rely on the dashboard having no gated content, so the authorized constructor is fine and this test is redundant-but-harmless. If the constructor's `SetAuthorized` makes "anonymous" impossible in one class, SKIP this step for Dashboard and instead cover anonymous rendering in Tasks 3–5 where gating actually exists.) **Decision: SKIP this step** — Dashboard/Sources/Machines/MachineDetail have no gated content, so an anonymous render differs in nothing from authorized; the contract test (Step 1) is the meaningful guarantee for these four. Anonymous-vs-authorized render tests are added in Tasks 3–5 where there is gated content to assert on.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs
git commit -m "feat(web) make read-only admin pages public-read; rewrite auth contract test" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 3: Document Triage — gate mutations, go public-read

Make `/admin/document-triage` public-read; gate the Relink / MarkGeneric actions both in the UI (`_isAdmin`) and server-side (`AdminActionGuard`).

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor`
- Modify: `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs` (add to the public list)
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminDocumentTriageTests.cs`

**Interfaces:**
- Consumes: `AdminActionGuard` (Task 1).

- [ ] **Step 1: Write the failing anonymous/authorized render tests**

Read the existing `AdminDocumentTriageTests.cs` to match its fixture/registration pattern (it registers `IRawDocumentRepository`, `IDocumentLinker`, `ISnackbar` doubles and calls `this.AddAuthorization().SetAuthorized(...)`). Add: (a) register `AdminActionGuard` in the existing setup so the page resolves it, and (b) two tests. The action button carries `data-testid="triage-action-relink"` / `triage-action-markgeneric` — if the page doesn't yet have those test ids, add them in Step 3.

Add to the test class:

```csharp
[Fact]
public void Authorized_RendersActionButtons()
{
    // existing setup authorizes the user; assert the gated actions render.
    var cut = RenderWithPopover<AdminDocumentTriage>();
    cut.WaitForAssertion(() =>
        Assert.NotEmpty(cut.FindAll("[data-testid='triage-action-relink']")));
}
```

For the anonymous case, add a SECOND test class in the same file that does NOT authorize:

```csharp
public sealed class AdminDocumentTriageAnonymousTests : AsyncBunitContext
{
    public AdminDocumentTriageAnonymousTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization(); // NOT authorized → _isAdmin false
        Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();
        // Register the same IRawDocumentRepository / IDocumentLinker / ISnackbar
        // doubles the authorized class uses (copy that registration here, returning
        // one triage row), then BunitNavigationManager.
        // ...repository doubles per the existing class...
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void Anonymous_ShowsQueue_HidesActions()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        cut.WaitForAssertion(() =>
        {
            // Read content present (a queue row's document id / source url).
            Assert.Contains("doc_triage_1", cut.Markup, StringComparison.Ordinal);
            // Gated actions absent.
            Assert.Empty(cut.FindAll("[data-testid='triage-action-relink']"));
            Assert.Empty(cut.FindAll("[data-testid='triage-action-markgeneric']"));
        });
    }
}
```

(The implementer copies the repository-double registration from the existing authorized class into the anonymous class so both render the same queue data; only the auth state differs. `AdminActionGuard` resolves the bUnit `IAuthorizationService` — authorized → `_isAdmin` true; not authorized → false.)

Also register `AdminActionGuard` in the EXISTING authorized `AdminDocumentTriageTests` constructor:
```csharp
Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminDocumentTriage"`
Expected: FAIL — `AdminActionGuard` not injected by the page yet; no `triage-action-*` test ids; anonymous test sees the action buttons (page still page-level `[Authorize]`, so it may not even render anonymous).

- [ ] **Step 3: Gate the page**

Edit `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor`:

1. Line 5: replace `@attribute [Authorize(Policy = "AdminOnly")]` with `@attribute [AllowAnonymous]`.
2. Add usings + inject + cascading auth state near the other `@inject` lines:
```razor
@using Microsoft.AspNetCore.Components.Authorization
@inject PinballWizard.Web.Security.AdminActionGuard Guard
```
and in `@code`:
```csharp
    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    private bool _isAdmin;
```
3. Resolve `_isAdmin` in `LoadAsync` (it already runs from `OnAfterRenderAsync`). At the top of `LoadAsync`’s `try`, add:
```csharp
            _isAdmin = await Guard.IsAdminAsync(AuthState);
```
4. Wrap each action button (Relink, MarkGeneric) so it only renders for admins, and add the test ids. The action cell currently renders the buttons unconditionally — wrap them:
```razor
@if (_isAdmin)
{
    <MudButton data-testid="triage-action-relink" ... OnClick="@(() => RelinkAsync(context.Item))">Relink</MudButton>
    <MudButton data-testid="triage-action-markgeneric" ... OnClick="@(() => MarkGenericAsync(context.Item))">Mark generic</MudButton>
}
```
(Match the existing button markup/params; only add the `@if (_isAdmin)` wrapper and the `data-testid`s.)
5. Server-side guard: at the very top of `RelinkAsync` and `MarkGenericAsync`, before any work:
```csharp
        if (!await Guard.IsAdminAsync(AuthState))
        {
            Snackbar.Add("Sign in as an administrator to perform this action.", Severity.Warning);
            return;
        }
```

- [ ] **Step 4: Add the page to the contract test public list**

In `AuthorizationContractTests.cs`, add to the `ShowcaseAdminPage_IsAllowAnonymous` `[Theory]`:
```csharp
    [InlineData(typeof(AdminDocumentTriage))]
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminDocumentTriage|FullyQualifiedName~AuthorizationContractTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs tests/PinballWizard.Web.Tests/Components/Admin/AdminDocumentTriageTests.cs
git commit -m "feat(web) document triage: public-read with gated relink/mark-generic actions" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 4: Link Overrides — gate mutations, redact identity, go public-read

Make `/admin/link-overrides` public-read; gate New Override + Delete; **redact `CreatedBy` and `Notes` columns** for anonymous viewers (identity/operator notes).

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor`
- Modify: `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminLinkOverridesTests.cs`

**Interfaces:**
- Consumes: `AdminActionGuard` (Task 1).

- [ ] **Step 1: Write the failing anonymous/authorized tests**

Register `AdminActionGuard` in the existing authorized `AdminLinkOverridesTests` setup. Add an authorized assertion that the New Override button + Delete action + `Created By` column header render. Add an `AdminLinkOverridesAnonymousTests` class (same pattern as Task 3) that does NOT authorize and asserts:
```csharp
[Fact]
public void Anonymous_ShowsRows_HidesActionsAndIdentity()
{
    var cut = RenderWithPopover<AdminLinkOverrides>();
    cut.WaitForAssertion(() =>
    {
        // override data present (the seeded source pattern)
        Assert.Contains("sternpinball.com/x", cut.Markup, StringComparison.Ordinal);
        // gated: no New Override button, no Delete, no identity columns
        Assert.Empty(cut.FindAll("[data-testid='overrides-new-button']"));
        Assert.Empty(cut.FindAll("[data-testid='overrides-delete']"));
        Assert.DoesNotContain("Created By", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("admin (local-dev)", cut.Markup, StringComparison.Ordinal);
    });
}
```
(The seeded override's `CreatedBy` is `"admin (local-dev)"` per `AdminTestDoubles`; assert it is absent for anonymous.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminLinkOverrides"`
Expected: FAIL.

- [ ] **Step 3: Gate the page**

Edit `src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor`:

1. Line 5: `@attribute [Authorize(Policy = "AdminOnly")]` → `@attribute [AllowAnonymous]`.
2. Add usings/inject/state (as Task 3): `@using Microsoft.AspNetCore.Components.Authorization`, `@inject PinballWizard.Web.Security.AdminActionGuard Guard`, `[CascadingParameter] Task<AuthenticationState>? AuthState`, `private bool _isAdmin;`. Resolve `_isAdmin = await Guard.IsAdminAsync(AuthState);` at the top of `LoadAsync`’s try.
3. Gate the "New Override" header button — wrap in `@if (_isAdmin)` and add `data-testid="overrides-new-button"`:
```razor
@if (_isAdmin)
{
    <MudButton data-testid="overrides-new-button" Variant="Variant.Filled" Color="Color.Primary"
               StartIcon="@Icons.Material.Filled.Add" OnClick="OpenCreateDialogAsync">New Override</MudButton>
}
```
4. **Redact identity columns**: wrap the `CreatedBy` and `Notes` `PropertyColumn`s in `@if (_isAdmin)`:
```razor
@if (_isAdmin)
{
    <PropertyColumn Property="x => x.CreatedBy" Title="Created By" />
}
<PropertyColumn Property="x => x.CreatedAt" Title="Created At" />
@if (_isAdmin)
{
    <PropertyColumn Property="x => x.Notes" Title="Notes" />
}
```
(SourcePattern, MachineIds, CreatedAt stay unconditional.)
5. Gate the Actions (Delete) column: wrap the `<TemplateColumn Title="Actions">` in `@if (_isAdmin)`, and add `data-testid="overrides-delete"` to the Delete button.
6. Gate the create dialog: wrap the `<MudDialog @bind-Visible="_showCreateDialog">…</MudDialog>` block in `@if (_isAdmin)` (so anonymous can't even instantiate it).
7. Server-side guards: at the top of `ConfirmCreateAsync` and `DeleteAsync`:
```csharp
        if (!await Guard.IsAdminAsync(AuthState))
        {
            Snackbar.Add("Sign in as an administrator to perform this action.", Severity.Warning);
            return;
        }
```

- [ ] **Step 4: Add to the contract test public list**

In `AuthorizationContractTests.cs` add:
```csharp
    [InlineData(typeof(AdminLinkOverrides))]
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminLinkOverrides|FullyQualifiedName~AuthorizationContractTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs tests/PinballWizard.Web.Tests/Components/Admin/AdminLinkOverridesTests.cs
git commit -m "feat(web) link overrides: public-read with gated actions + identity redaction" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 5: Settings — read-only values public, gate edits + provenance + Prompt Templates tab

Make `/admin/settings` public-read showing the live VALUES read-only; gate all editing, the Save/Reset bar, the **provenance lines**, and the **entire Prompt Templates tab**.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSettings.razor`
- Modify: `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSettingsTests.cs`

**Interfaces:**
- Consumes: `AdminActionGuard` (Task 1).

- [ ] **Step 1: Write the failing anonymous/authorized tests**

Register `AdminActionGuard` in the existing authorized `AdminSettingsTests` setup. Keep its existing authorized assertions (edit controls present). Add an `AdminSettingsAnonymousTests` class (not authorized) asserting:
```csharp
[Fact]
public void Anonymous_ShowsValuesReadOnly_HidesEditsAndPromptsAndProvenance()
{
    var cut = RenderWithPopover<AdminSettings>();
    cut.WaitForAssertion(() =>
    {
        // value shown read-only
        Assert.NotEmpty(cut.FindAll("[data-testid='confidence-value-readonly']"));
        // edit controls + save + reset absent
        Assert.Empty(cut.FindAll("[data-testid='save-button']"));
        Assert.Empty(cut.FindAll("[data-testid='reset-ai.confidence_threshold']"));
        // Prompt Templates tab absent
        Assert.DoesNotContain("Prompt Templates", cut.Markup, StringComparison.Ordinal);
        // provenance absent
        Assert.Empty(cut.FindAll("[data-testid^='provenance-']"));
    });
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminSettings"`
Expected: FAIL.

- [ ] **Step 3: Gate the page**

Edit `src/PinballWizard.Web/Components/Pages/Admin/AdminSettings.razor`:

1. Line 4: `@attribute [Authorize(Policy = "AdminOnly")]` → `@attribute [AllowAnonymous]`. Update the header comment block (lines 7-22) to note: public-read showcase showing live values; edits/provenance/Prompt Templates gated to `AdminOnly` via `_isAdmin` + `AdminActionGuard`.
2. Add `@using Microsoft.AspNetCore.Components.Authorization` (the page already has `@using` lines) and `@inject PinballWizard.Web.Security.AdminActionGuard Guard`. The page already has `[CascadingParameter] Task<AuthenticationState>? AuthState`. Add `private bool _isAdmin;`. Resolve it in `LoadAsync` (top of try): `_isAdmin = await Guard.IsAdminAsync(AuthState);`.
3. For each editable setting (Confidence slider, Cost ceiling numeric, Max turns, Top-K, Min score): render the editable control + `ResetButton` only when `_isAdmin`; otherwise render a read-only value. Pattern for the confidence row (apply the analogous change to all five):
```razor
@if (_isAdmin)
{
    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="4">
        <MudSlider T="double" @bind-Value="conf.NumericValue" Min="0.3" Max="0.95" Step="0.01"
                   Class="flex-grow-1" aria-label="Confidence threshold" />
        <MudText Typo="Typo.h6" data-testid="confidence-value">@conf.NumericValue.ToString("0.00")</MudText>
        @ResetButton(conf)
    </MudStack>
}
else
{
    <MudText Typo="Typo.h6" data-testid="confidence-value-readonly">@conf.NumericValue.ToString("0.00")</MudText>
}
@ProvenanceLine(conf)
```
For the numeric settings (ceiling, turns, topK) the read-only branch renders `<MudText data-testid="<key>-value-readonly">@row.NumericValue.ToString(...)</MudText>`; min-score mirrors confidence. Use a distinct `-value-readonly` testid per setting.
4. **Provenance redaction:** change `ProvenanceLine` so it only emits for admins. Edit the `ProvenanceLine` `RenderFragment` (line 428) to guard on `_isAdmin`:
```csharp
    private RenderFragment ProvenanceLine(SettingRow row) => __builder =>
    {
        @if (_isAdmin && row.HasOverride)
        {
            <MudText Typo="Typo.caption" Color="Color.Secondary"
                     data-testid="@($"provenance-{row.Key}")">
                Overridden by @row.UpdatedBy on @row.UpdatedAtUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'")
            </MudText>
        }
    };
```
5. **Hide the Save/Reset bar** for anonymous: wrap the bottom `<MudStack Row="true" … Class="mt-6">…Save changes…</MudStack>` (lines 262-275) in `@if (_isAdmin)`.
6. **Hide the Prompt Templates tab:** wrap the entire `<MudTabPanel Text="Prompt Templates" …>…</MudTabPanel>` (lines 171-259) in `@if (_isAdmin)`.
7. Server-side guards: add at the top of `SaveAsync`, `ResetAsync`, `SaveNewPromptVersionAsync`, `ActivatePromptVersionAsync`, `RevertPromptToDefaultAsync`:
```csharp
        if (!await Guard.IsAdminAsync(AuthState))
        {
            Snackbar.Add("Sign in as an administrator to perform this action.", Severity.Warning);
            return;
        }
```
(`ResetAsync` returns `Task`; the guard fits. For `SaveAsync` place it before `_saving = true`.)

- [ ] **Step 4: Add to the contract test public list**

In `AuthorizationContractTests.cs` add:
```csharp
    [InlineData(typeof(AdminSettings))]
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminSettings|FullyQualifiedName~AuthorizationContractTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminSettings.razor tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs tests/PinballWizard.Web.Tests/Components/Admin/AdminSettingsTests.cs
git commit -m "feat(web) settings: public-read values; gate edits, provenance, prompt templates" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 6: Showcase surfacing — public nav entry + admin read-only banner

Add the "Behind the Scenes" entry to the public header and a read-only banner + sign-in/out affordance to the admin layout.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Theming/BrandHeader.razor`
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Theming/BrandHeaderTests.cs` (create or extend) and an `AdminLayout` banner test.

- [ ] **Step 1: Write the failing tests**

Create/extend `tests/PinballWizard.Web.Tests/Components/Theming/BrandHeaderTests.cs`:
```csharp
[Fact]
public void BrandHeader_RendersBehindTheScenesLink()
{
    var cut = Render<PinballWizard.Web.Components.Theming.BrandHeader>();
    var link = cut.Find("a[href='/admin']");
    Assert.Contains("Behind the Scenes", link.TextContent, StringComparison.Ordinal);
}
```
(Use the project's bUnit base; `BrandHeader` uses `MudLink`/`MudButton` — render with `AddMudServices()` and a `MudPopoverProvider` sibling if needed.)

For `AdminLayout`, add a test asserting the anonymous banner renders the sign-in link `a[href='/MicrosoftIdentity/Account/SignIn']` with `data-testid="admin-readonly-banner"`, and that an authorized render hides the banner. (Match the existing AdminLayout test pattern if one exists; AdminLayout requires the MudBlazor providers + an auth context.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~BrandHeaderTests|FullyQualifiedName~AdminLayout"`
Expected: FAIL — link/banner not present yet.

- [ ] **Step 3: Add the nav entry**

In `src/PinballWizard.Web/Components/Theming/BrandHeader.razor`, inside the `<nav>` add a second button before/after "What we cover":
```razor
    <MudButton Href="/admin"
               Variant="Variant.Text"
               Color="Color.Inherit"
               Class="nav-link"
               aria-label="Behind the Scenes — admin showcase">
        Behind the Scenes
    </MudButton>
```
Update the stale comment line "Authenticated routes (/admin/*) are surfaced only in AdminLayout's drawer." → "The read-only admin showcase is surfaced here ('Behind the Scenes' → /admin); mutations there are gated to signed-in admins."

- [ ] **Step 4: Add the admin read-only banner**

In `src/PinballWizard.Web/Components/Layout/AdminLayout.razor`, add `@using Microsoft.AspNetCore.Components.Authorization`, and inside `<MudMainContent>` above `<TiltErrorBoundary>` add:
```razor
<AuthorizeView Policy="AdminOnly">
    <NotAuthorized>
        <MudAlert Severity="Severity.Info" Dense="true" Class="ma-2"
                  data-testid="admin-readonly-banner">
            Read-only view —
            <MudLink Href="/MicrosoftIdentity/Account/SignIn" Color="Color.Inherit">
                <b>sign in</b>
            </MudLink>
            to manage.
        </MudAlert>
    </NotAuthorized>
    <Authorized>
        <MudStack Row="true" AlignItems="AlignItems.Center" Class="ma-2" Spacing="2">
            <MudText Typo="Typo.caption" data-testid="admin-identity">
                Signed in as @context.User.Identity?.Name
            </MudText>
            <MudLink Href="/MicrosoftIdentity/Account/SignOut" Typo="Typo.caption">Sign out</MudLink>
        </MudStack>
    </Authorized>
</AuthorizeView>
```
(`AuthorizeView` here is fine — the layout's MudBlazor providers are already pinned InteractiveServer. On the local-dev permissive path `AdminOnly` passes, so the `Authorized` branch shows; in prod anonymous → `NotAuthorized` banner.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~BrandHeaderTests|FullyQualifiedName~AdminLayout"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Theming/BrandHeader.razor src/PinballWizard.Web/Components/Layout/AdminLayout.razor tests/PinballWizard.Web.Tests/Components/Theming/BrandHeaderTests.cs tests/PinballWizard.Web.Tests/Components/Admin/
git commit -m "feat(web) surface admin as showcase: Behind the Scenes nav + read-only banner" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 7: Full-suite verification + pre-push self-audit

**Files:** none (verification only)

- [ ] **Step 1: Full Web test project**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj`
Expected: PASS — in particular `AuthorizationContractTests` (all 7 admin pages `[AllowAnonymous]` + the exactly-one-classification invariant), the anonymous/authorized render tests (Triage/LinkOverrides/Settings), `AdminAccessibilityTests` (axe clean on the public render), `AdminActionGuardTests`.

- [ ] **Step 2: Solution build (`-warnaserror`)**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Pre-push self-audit (BLOCKING)**

Run `/local-review` and `/standards-audit`. Treat 🔴 as blocking. Confirm specifically:
- **No surface over-exposed:** every gated `data-testid` (mutations, identity/provenance, Prompt Templates) is asserted ABSENT in an anonymous render test (cat 8 / security).
- **Server-side guard present** on every mutation handler (Triage relink/markgeneric; LinkOverrides create/delete; Settings save/reset/prompt save/activate/revert) — not UI-only gating (Invariant: defense-in-depth).
- No operator identity reaches an anonymous render (Settings provenance, LinkOverrides CreatedBy/Notes).
- No new cross-partition query; no provenance fields dropped from persisted data (read-only display only).

- [ ] **Step 4: Manual smoke (recommended)**

Locally (permissive path) confirm `/admin` is reachable from the "Behind the Scenes" nav entry and all surfaces render with controls (local = admin). The anonymous/prod gating is proven by the bUnit tests (local-dev is permissive by design).

---

## Notes for the implementer

- **Why `_isAdmin` flag rather than `<AuthorizeView>` inside pages:** grids (MudDataGrid columns) and per-row buttons gate more cleanly with a resolved boolean than with nested `AuthorizeView` fragments, and the flag is trivially assertable in bUnit. It resolves the SAME `AdminOnly` policy via `AdminActionGuard`. `AuthorizeView` is still used in `AdminLayout` (Task 6) where it's the natural fit. Either way, the **server-side guard in handlers is the real boundary** — the flag/AuthorizeView only hides UI.
- **Local-dev is permissive** (`AdminOnly` = `RequireAssertion(true)`), so `_isAdmin` is true locally and you'll see all controls — that's intended (fully-functional local dev). The bUnit tests set authorization explicitly to prove both anonymous and authorized states.
- **bUnit auth doubles:** `this.AddAuthorization()` with no `SetAuthorized` → not authorized → `_isAdmin` false. `this.AddAuthorization().SetAuthorized("name")` → authorized → policy passes → `_isAdmin` true. Every test class that renders a gated page must also `Services.AddScoped<AdminActionGuard>()`.
- **Commit safety:** every task flips a page to `[AllowAnonymous]` only together with its per-control gating + the contract-test public-list entry, so each commit is both green and non-exposing.
