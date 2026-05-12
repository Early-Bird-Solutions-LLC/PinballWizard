using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using PinballWizard.Web.Components.Pages;
using IndexPage = PinballWizard.Web.Components.Pages.Index;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Security;

// Authorization contract tests — pin the route-level authorization structure.
//
// PinballWizard uses a FallbackPolicy (RequireAuthenticatedUser) in Program.cs
// so that any Blazor route without [AllowAnonymous] is automatically protected.
// Public routes opt out with [AllowAnonymous]; no per-page [Authorize] is needed
// on admin routes because the FallbackPolicy covers them.
//
// These tests use reflection on the component types rather than spinning up a
// TestServer. This avoids OIDC metadata discovery against the placeholder tenant
// while still pinning the contract that matters: if someone accidentally adds
// [AllowAnonymous] to an admin page, or forgets [AllowAnonymous] on a new
// public page, these tests catch it immediately.
//
// Invariant: adding a new /admin/* page that lacks [AllowAnonymous] is SAFE
// (FallbackPolicy protects it). Adding a new public page that lacks
// [AllowAnonymous] is UNSAFE (FallbackPolicy would challenge anonymous users).
// The second check is the critical one.
public sealed class AuthorizationContractTests
{
    // ── Admin pages must NOT carry [AllowAnonymous] ────────────────────────
    // The FallbackPolicy covers these. Adding [AllowAnonymous] would
    // silently bypass auth for that page without any compile-time warning.

    [Theory]
    [InlineData(typeof(AdminDashboard))]
    [InlineData(typeof(AdminMachines))]
    [InlineData(typeof(AdminSources))]
    public void AdminPage_DoesNotHaveAllowAnonymous(Type page)
    {
        Assert.Null(page.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ── Admin pages must NOT carry redundant [Authorize] ──────────────────
    // The FallbackPolicy makes per-page [Authorize] misleading — it implies
    // the page is unprotected without it, which is false. Keep admin pages
    // clean: protected by policy, no redundant attribute.

    [Theory]
    [InlineData(typeof(AdminDashboard))]
    [InlineData(typeof(AdminMachines))]
    [InlineData(typeof(AdminSources))]
    public void AdminPage_DoesNotHaveRedundantAuthorize(Type page)
    {
        Assert.Null(page.GetCustomAttribute<AuthorizeAttribute>());
    }

    // ── Public pages MUST carry [AllowAnonymous] ──────────────────────────
    // Without [AllowAnonymous], the FallbackPolicy challenges anonymous
    // users on what should be public routes. This is the critical invariant:
    // every public page must explicitly opt out of the FallbackPolicy.
    //
    // NotFound (/{**slug} catch-all) is included — if it were missing
    // [AllowAnonymous], any unrecognised URL for an unauthenticated user
    // would challenge instead of showing the 404 page.

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
