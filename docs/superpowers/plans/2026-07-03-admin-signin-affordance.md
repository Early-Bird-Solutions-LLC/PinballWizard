# Admin Sign-In Affordance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface the already-wired Entra sign-in in the admin UI — make the console-log "sign in" notice a working link, and add a general Sign in / Sign out control to the admin app bar.

**Architecture:** A pure `AdminSignIn` helper builds the Microsoft.Identity.Web AccountController sign-in URL with a return path. A small anchor-only `AdminIdentityControl` component renders Sign in (anonymous) or "Signed in as … / Sign out" (authed) in the `AdminLayout` app bar. The `AdminJobExecutionDetail` `exec-log-signin` notice becomes a link built by the same helper. No posture change (pages stay `[AllowAnonymous]`), no new backend, no security-boundary change.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor, Microsoft.Identity.Web / Microsoft.Identity.Web.UI 4.10.0, xUnit + bUnit + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-07-03-admin-signin-affordance-design.md`

**Working directory:** the `feat/admin-signin-affordance` worktree (a concurrent session owns the main tree). All paths below are relative to the repo root of that worktree.

## Global Constraints

- **No posture change.** `/admin/*` stays `[AllowAnonymous]`; do not add `[Authorize]`/`FallbackPolicy`. The security boundary remains `AdminActionGuard` + the `AdminOnly` (`RequireRole("GlobalAdmin")`) policy — untouched.
- **Anchor-only chrome.** The identity control uses `Href` navigation only — **no `@onclick`, no `@rendermode`** — to avoid the interactive-island-in-static-layout circuit break that has hit `AdminLayout` before.
- **MudBlazor-strict** (ADR-0008); theme tokens only — no hardcoded colours; use `Color.Inherit`/`Color.*`.
- **Personal identity commits:** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer.**
- **Build gate:** `dotnet build PinballWizard.slnx -warnaserror` must stay clean.
- **No-guessing:** the AccountController return-URL query-parameter name is verified against Microsoft.Identity.Web.UI 4.10.0 in Task 1 Step 1 before it is written into code.

---

### Task 1: `AdminSignIn` helper (pure) + verify the return-URL param

Centralises the Microsoft.Identity.Web account paths and builds the sign-in URL with a return path.

**Files:**
- Create: `src/PinballWizard.Web/Security/AdminSignIn.cs`
- Test: `tests/PinballWizard.Web.Tests/Security/AdminSignInTests.cs`

**Interfaces:**
- Produces: `static class AdminSignIn` with `const string SignInPath`, `const string SignOutPath`, and `static string Href(string? returnUrl)`.

- [ ] **Step 1: Verify the return-URL query-param name (no-guessing gate)**

The `Microsoft.Identity.Web.UI` `AccountController.SignIn` action determines whether a return URL
can be passed as a query param and what it is called. Verify against the pinned version 4.10.0:

Read the packaged controller source from the NuGet cache:
```bash
ls ~/.nuget/packages/microsoft.identity.web.ui/4.10.0/
# Inspect the AccountController.SignIn action (decompiled/source) OR the package's docs for 4.10.0.
```
Record the finding in the header comment of `AdminSignIn.cs`:
- **If** `SignIn` accepts a return-URL query param (expected name `redirectUri`) and challenges with it → use that exact name for `ReturnUrlParam` (Step 2).
- **If** `SignIn` ignores query input and always redirects to `~/` → set `ReturnUrlParam = null` semantics: `Href` returns the **bare** `SignInPath` (post-login lands on home; document this in the comment), and the Step-3 test for the return-URL case asserts the bare path instead. Do NOT invent a param the controller ignores.

The steps below assume the expected outcome (`redirectUri` honored). If Step 1 shows otherwise, adjust the constant/test per the branch above.

- [ ] **Step 2: Write the failing test**

`tests/PinballWizard.Web.Tests/Security/AdminSignInTests.cs`:
```csharp
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Security;

public sealed class AdminSignInTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Href_NoReturnUrl_IsBareSignInPath(string? returnUrl) =>
        Assert.Equal(AdminSignIn.SignInPath, AdminSignIn.Href(returnUrl));

    [Fact]
    public void Href_WithReturnUrl_AppendsEncodedRedirect()
    {
        var href = AdminSignIn.Href("/admin/jobs/j/executions/e");
        Assert.Equal(
            "/MicrosoftIdentity/Account/SignIn?redirectUri=%2Fadmin%2Fjobs%2Fj%2Fexecutions%2Fe",
            href);
    }

    [Fact]
    public void Paths_AreTheMicrosoftIdentityAccountEndpoints()
    {
        Assert.Equal("/MicrosoftIdentity/Account/SignIn", AdminSignIn.SignInPath);
        Assert.Equal("/MicrosoftIdentity/Account/SignOut", AdminSignIn.SignOutPath);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminSignInTests"`
Expected: FAIL — `AdminSignIn` does not exist.

- [ ] **Step 4: Write the helper**

`src/PinballWizard.Web/Security/AdminSignIn.cs`:
```csharp
using System;

namespace PinballWizard.Web.Security;

// Microsoft.Identity.Web account endpoints + sign-in URL builder. The
// AccountController (Microsoft.Identity.Web.UI 4.10.0) is registered via
// AddMicrosoftIdentityUI() and mapped by app.MapControllers(); AdminLayout's
// working "Sign out" link already proves these paths resolve.
//
// Return-URL param 'redirectUri' VERIFIED against Microsoft.Identity.Web.UI
// 4.10.0 AccountController.SignIn in plan Task 1.
public static class AdminSignIn
{
    public const string SignInPath = "/MicrosoftIdentity/Account/SignIn";
    public const string SignOutPath = "/MicrosoftIdentity/Account/SignOut";

    private const string ReturnUrlParam = "redirectUri";

    // Sign-in URL that returns to `returnUrl` (a LOCAL relative path such as
    // "/admin/jobs/..."). Bare path when returnUrl is null/whitespace.
    public static string Href(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl)
            ? SignInPath
            : $"{SignInPath}?{ReturnUrlParam}={Uri.EscapeDataString(returnUrl)}";
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminSignInTests"`
Expected: PASS (3 tests / 5 cases).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Security/AdminSignIn.cs tests/PinballWizard.Web.Tests/Security/AdminSignInTests.cs
git commit -m "feat(web) AdminSignIn helper: Identity.Web account paths + return-url sign-in link"
```

---

### Task 2: `AdminIdentityControl` component + wire into the `AdminLayout` app bar

Anchor-only identity control; replaces the low-visibility caption strip in `MudMainContent`.

**Files:**
- Create: `src/PinballWizard.Web/Components/Layout/AdminIdentityControl.razor`
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` (add control to app bar; remove old `<AuthorizeView>` strip at lines 67-76)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminIdentityControlTests.cs`

**Interfaces:**
- Consumes: `AdminSignIn.Href` / `AdminSignIn.SignOutPath` (Task 1); injected `NavigationManager`.
- Produces: component `AdminIdentityControl` (namespace `PinballWizard.Web.Components.Layout`); testids `admin-signin` (anonymous), `admin-identity` + `admin-signout` (authed).

- [ ] **Step 1: Check for existing `admin-identity` references**

Run: `git grep -n "admin-identity" -- '*.cs' '*.razor'`
Expected: only the current `AdminLayout.razor` occurrence. If any test references it, note it — the testid is preserved in the new component so such tests keep passing. (If a test asserts the identity is inside `MudMainContent`, update its query to the new location.)

- [ ] **Step 2: Write the failing tests**

`tests/PinballWizard.Web.Tests/Components/Admin/AdminIdentityControlTests.cs`:
```csharp
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class AdminIdentityControlTests : Bunit.TestContext
{
    public AdminIdentityControlTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudBlazor 9 wants a MudPopoverProvider present when MudBlazor components render
    // (reference_mudblazor9_bunit_popover_provider) — render it as a sibling.
    private IRenderedComponent<AdminIdentityControl> RenderControl()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminIdentityControl>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminIdentityControl>();
    }

    [Fact]
    public void Anonymous_RendersSignIn_ToSignInEndpoint()
    {
        this.AddAuthorization().SetNotAuthorized();
        var cut = RenderControl();
        var link = cut.Find("[data-testid='admin-signin']");
        Assert.StartsWith(AdminSignIn.SignInPath, link.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='admin-identity']"));
        Assert.Empty(cut.FindAll("[data-testid='admin-signout']"));
    }

    [Fact]
    public void Authenticated_RendersIdentityAndSignOut()
    {
        this.AddAuthorization().SetAuthorized("jim@example.com");
        var cut = RenderControl();
        cut.Find("[data-testid='admin-identity']");
        var signOut = cut.Find("[data-testid='admin-signout']");
        Assert.Equal(AdminSignIn.SignOutPath, signOut.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='admin-signin']"));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminIdentityControlTests"`
Expected: FAIL — `AdminIdentityControl` does not exist.

- [ ] **Step 4: Write the component**

`src/PinballWizard.Web/Components/Layout/AdminIdentityControl.razor`:
```razor
@using Microsoft.AspNetCore.Components.Authorization
@using PinballWizard.Web.Security
@inject NavigationManager Nav

@* Admin app-bar identity control. Anchor-only (no @onclick / @rendermode) so it
 * never perturbs admin page circuits (interactive-island-in-static-layout).
 * Sign-in/out route through the Microsoft.Identity.Web AccountController via AdminSignIn. *@

<AuthorizeView>
    <Authorized>
        <MudText Typo="Typo.caption" Class="mr-2" data-testid="admin-identity">
            Signed in as @context.User.Identity?.Name
        </MudText>
        <MudButton Href="@AdminSignIn.SignOutPath"
                   Variant="Variant.Text" Color="Color.Inherit" Size="Size.Small"
                   StartIcon="@Icons.Material.Filled.Logout" data-testid="admin-signout">
            Sign out
        </MudButton>
    </Authorized>
    <NotAuthorized>
        <MudButton Href="@AdminSignIn.Href(ReturnUrl)"
                   Variant="Variant.Text" Color="Color.Inherit" Size="Size.Small"
                   StartIcon="@Icons.Material.Filled.Login" data-testid="admin-signin">
            Sign in
        </MudButton>
    </NotAuthorized>
</AuthorizeView>

@code {
    // Current local path so sign-in returns the user to the page they were on.
    private string ReturnUrl => "/" + Nav.ToBaseRelativePath(Nav.Uri);
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminIdentityControlTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Wire into `AdminLayout` app bar and remove the old strip**

In `src/PinballWizard.Web/Components/Layout/AdminLayout.razor`:

(a) Add the control to the `MudAppBar`, between `<MudSpacer />` and the "Back to Wizard" button:
```razor
        <MudSpacer />

        <AdminIdentityControl />

        <MudButton Href="/"
                   Variant="Variant.Text"
                   Color="Color.Inherit"
                   StartIcon="@Icons.Material.Filled.ArrowBack"
                   Size="Size.Small">
            Back to Wizard
        </MudButton>
```

(b) Remove the now-redundant identity strip in `MudMainContent` (the entire `<AuthorizeView>…</AuthorizeView>` block, lines 67-76), leaving:
```razor
    <MudMainContent>
        <TiltErrorBoundary>
            @Body
        </TiltErrorBoundary>
    </MudMainContent>
```
`AdminIdentityControl` is in the same namespace (`PinballWizard.Web.Components.Layout`) as `AdminLayout`, so no extra `@using` is needed.

- [ ] **Step 7: Build to verify layout compiles**

Run: `dotnet build src/PinballWizard.Web/PinballWizard.Web.csproj --nologo -warnaserror`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AdminIdentityControl.razor src/PinballWizard.Web/Components/Layout/AdminLayout.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminIdentityControlTests.cs
git commit -m "feat(web) admin app-bar Sign in / Sign out identity control"
```

---

### Task 3: Make the `exec-log-signin` notice a sign-in link

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor` (lines 66-68)
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs` (extend `Anonymous_SeesSignInNotice_NotLogs`)

**Interfaces:**
- Consumes: `AdminSignIn.Href` (Task 1); the page's existing `JobName` / `ExecutionName` params.
- Produces: the `exec-log-signin` alert contains an `<a>` whose href starts with `AdminSignIn.SignInPath`.

- [ ] **Step 1: Extend the failing test**

In `AdminJobExecutionDetailTests.cs`, add a `using PinballWizard.Web.Security;` (if absent) and append to `Anonymous_SeesSignInNotice_NotLogs` (after the existing `exec-log-signin` find):
```csharp
        var signInLink = cut.Find("[data-testid='exec-log-signin'] a");
        Assert.StartsWith(AdminSignIn.SignInPath, signInLink.GetAttribute("href"));
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests.Anonymous_SeesSignInNotice_NotLogs"`
Expected: FAIL — no `<a>` inside the notice.

- [ ] **Step 3: Make the notice a link**

In `AdminJobExecutionDetail.razor`, replace the alert body (lines 66-68):
```razor
            <MudAlert Severity="Severity.Info" data-testid="exec-log-signin">
                <MudLink Href="@AdminSignIn.Href($"/admin/jobs/{JobName}/executions/{ExecutionName}")">
                    Sign in as an admin
                </MudLink> to view this run's console logs.
            </MudAlert>
```
`@using PinballWizard.Web.Security` is already present in this file (line 10), so `AdminSignIn` resolves.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: PASS (all existing + the extended assertion).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(web) make console-log sign-in notice a working sign-in link"
```

---

### Task 4: Full-suite gate + self-audit + PR

**Files:** none (verification + delivery).

- [ ] **Step 1: Zero-warning build**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: CI-equivalent test suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: all pass. (Catches any `RenderModeConventionTests` / contract test touching `AdminLayout`.)

- [ ] **Step 3: Pre-push self-audit**

Run `/local-review` and `/standards-audit`. Applicable standards: frontend-blazor (new `.razor`), testing, delivery. Fix every 🔴.

- [ ] **Step 4: Operational hand-off verification (live)**

Using the smoke rig (`%LOCALAPPDATA%\Temp\pinwiz-smoke`, headed Edge — see `reference_pinwiz_smoke_automation`): open a deployed admin page and confirm the **Sign in** button appears in the app bar and the console-log notice is a link; clicking either navigates to the Entra sign-in (`/MicrosoftIdentity/Account/SignIn`). Completing the Entra credential is manual. (bUnit already proves the anonymous/authed rendering.)

- [ ] **Step 5: Ship**

Create the PR with `gh pr create`, add + verify the `claude-code` label, then triage post-push code-scanning per `.claude/PR-AUDIT.md` Step 2. PR description records the `/local-review` + `/standards-audit` outcomes and links the spec.

---

## Notes for the implementer

- `AsyncBunitContext`, `AddAuthorization().SetNotAuthorized()` / `.SetAuthorized(...).SetPolicies(AuthorizationPolicies.AdminOnly)`, `JSRuntimeMode.Loose`, and the `MudPopoverProvider` sibling-render helper are all established in `AdminJobExecutionDetailTests.cs` — copy their exact usings/patterns. (`AdminIdentityControlTests` uses `Bunit.TestContext` because the control is synchronous; that is fine.)
- `MudButton Href="…"` renders an anchor (`<a href>`) — full-page navigation to the AccountController, which is what leaves the Blazor circuit to perform the OIDC round-trip. Do NOT convert it to `OnClick`.
- Do not add `@rendermode` to `AdminIdentityControl` or the app bar — the control must stay static-safe.
