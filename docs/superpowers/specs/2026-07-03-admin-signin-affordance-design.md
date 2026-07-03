# Admin Sign-In Affordance — Design

**Date:** 2026-07-03
**Status:** Approved (brainstorming)
**Topic:** Surface the existing Entra sign-in flow in the admin chrome.

## Problem

The admin console-log panel (`AdminJobExecutionDetail`, PR #640) renders a plain-text notice
*"Sign in as an admin to view this run's console logs."* — it is not a link, and there is **no
general sign-in affordance anywhere in the admin UI**. `AdminLayout` has an `<AuthorizeView>` with
an `<Authorized>` branch ("Signed in as … / Sign out") but **no `<NotAuthorized>` branch**, so an
anonymous visitor can never trigger sign-in. Result: even a legitimate admin, arriving on an
`[AllowAnonymous]` admin page, has no way to authenticate and is stuck on the gated-panel notice.

## Non-goals / posture (unchanged)

- **Keep the public-read posture.** All `/admin/*` pages stay `[AllowAnonymous]` (documented showcase
  surface; ADR-0009). No `FallbackPolicy`, no route-level `[Authorize]` changes. The security
  boundary remains `AdminActionGuard` + the `AdminOnly` (`RequireRole("GlobalAdmin")`) policy — this
  change does not touch it.
- **Admin chrome only.** No sign-in control on the public wizard/`MainLayout`; only admin needs
  identity today.
- No new backend. The sign-in endpoint already exists via `Microsoft.Identity.Web.UI`
  (`AddMicrosoftIdentityWebApp` + `AddMicrosoftIdentityUI`, `app.MapControllers()`), proven by the
  working `/MicrosoftIdentity/Account/SignOut` link.

## Architecture

Three small units. The whole change is anchor-based navigation to the existing AccountController —
**no `@onclick`, no `@rendermode`** on any chrome, to avoid the interactive-island-in-static-layout
circuit break that has bitten `AdminLayout` before.

### Unit 1 — `AdminSignIn` static helper (`src/PinballWizard.Web/Security/AdminSignIn.cs`)

Pure, unit-tested. Centralises the Microsoft.Identity.Web paths so no call site hard-codes them.

```csharp
public static class AdminSignIn
{
    public const string SignInPath  = "/MicrosoftIdentity/Account/SignIn";
    public const string SignOutPath = "/MicrosoftIdentity/Account/SignOut";

    // Returns the sign-in URL that returns the user to `returnUrl` after the Entra round-trip.
    // Bare path when returnUrl is null/whitespace. returnUrl is a local relative path
    // (built from NavigationManager) — never an absolute/off-site URL.
    public static string Href(string? returnUrl); // e.g. "/MicrosoftIdentity/Account/SignIn?redirectUri=%2Fadmin%2Fjobs%2F..."
}
```

> **No-guessing gate (plan Task 1):** the return-URL query-parameter name is expected to be
> `redirectUri` for the `Microsoft.Identity.Web.UI` `AccountController`, but the **exact name and
> that it is honored** must be verified against the referenced package version (read the
> `AccountController.SignIn` action, or the package's public docs for that version) **before** it is
> written into `Href`. If the param differs, `Href` uses the verified name.

### Unit 2 — `AdminLayout` app-bar identity control

Replace the low-visibility caption strip currently in `MudMainContent` with an `<AuthorizeView>` in
the `MudAppBar`, to the right of "Back to Wizard":

- `<NotAuthorized>` → `<MudButton Href="@AdminSignIn.Href(_returnUrl)" StartIcon="@Icons.Material.Filled.Login" Variant="Variant.Text" Color="Color.Inherit" Size="Size.Small" data-testid="admin-signin">Sign in</MudButton>`
- `<Authorized>` → caption `Signed in as @context.User.Identity?.Name` (**keeps** `data-testid="admin-identity"`) + a Sign out `MudButton`/`MudLink` to `AdminSignIn.SignOutPath`.

`AdminLayout` injects `NavigationManager`; `_returnUrl` = current relative path
(`NavigationManager.ToBaseRelativePath(NavigationManager.Uri)` prefixed with `/`) so sign-in returns
to the page the user was on. The control is anchor-only (`Href`), so it works identically on static
(Dashboard, Sources) and interactive (Settings, Triage, …) admin pages.

### Unit 3 — `AdminJobExecutionDetail` log-notice link

The `exec-log-signin` alert keeps its `data-testid` but the text becomes a sentence with an inline
link:

> [Sign in as an admin](AdminSignIn.Href(returnToThisExecutionPage)) to view this run's console logs.

The return URL points back at the current execution-detail page so the user lands on the logs after
authenticating.

## Auth flow

1. Anonymous admin clicks **Sign in** (app bar) or the notice link.
2. Full-page navigation to `/MicrosoftIdentity/Account/SignIn?redirectUri=<current admin path>`
   (leaves the Blazor circuit — expected; it's a controller endpoint).
3. Entra OIDC round-trip → AccountController redirects back to the admin path.
4. On return, the `AdminOnly` (`GlobalAdmin`) `AuthorizeView`/`AdminActionGuard` now succeeds, and
   the gated content (e.g. the console logs) renders. The app bar now shows "Signed in as … / Sign out".

## Testing

- **Unit** (`AdminSignInTests`): `Href` encodes the return URL, uses the verified param name, and
  returns the bare path for null/empty input.
- **bUnit `AdminLayout`**: renders **Sign in** (`admin-signin`) when `NotAuthorized`; renders
  identity (`admin-identity`) + **Sign out** when `Authorized`. (Follows the existing
  `AddAuthorization().SetAuthorized(...).SetRoles("GlobalAdmin")` / `MudPopoverProvider`-sibling
  patterns.)
- **bUnit `AdminJobExecutionDetail`**: extend the existing anonymous test to assert the
  `exec-log-signin` notice now contains a link whose `href` starts with `AdminSignIn.SignInPath`.

## Files

| File | Change |
| --- | --- |
| `src/PinballWizard.Web/Security/AdminSignIn.cs` | **new** — pure href/paths helper |
| `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` | app-bar identity control (both branches); drop caption strip |
| `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor` | `exec-log-signin` notice → link |
| `tests/PinballWizard.Web.Tests/Security/AdminSignInTests.cs` | **new** — unit |
| `tests/PinballWizard.Web.Tests/Components/Admin/AdminLayoutTests.cs` | Sign in / Sign out branches (new or extend) |
| `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs` | assert notice link |

## Constraints

- Personal identity commits (`94459922+jkeeley2073@users.noreply.github.com`); no Claude attribution.
- MudBlazor-strict + `App*` wrappers where applicable; theme tokens only (no hardcoded colours).
- Zero-warning build (`dotnet build PinballWizard.slnx -warnaserror`).
- Delivered from the `feat/admin-signin-affordance` worktree (a concurrent session owns the main tree).
