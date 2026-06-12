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
// Public routes opt out with [AllowAnonymous].
//
// PR-B0 (2026-06-11 decision) supersedes the earlier "no redundant
// [Authorize] on admin pages" pin: admin pages now REQUIRE
// [Authorize(Policy = "AdminOnly")] (Wizard.Admin Entra app role). The
// FallbackPolicy only proves authentication; admin surfaces mutate live
// Wizard behavior and need the role. The attribute is therefore
// load-bearing, not redundant.
//
// These tests use reflection on the component types rather than spinning up a
// TestServer. This avoids OIDC metadata discovery against the placeholder tenant
// while still pinning the contract that matters: if someone accidentally adds
// [AllowAnonymous] to an admin page, forgets the AdminOnly policy on a new
// admin page, or forgets [AllowAnonymous] on a new public page, these tests
// catch it immediately.
public sealed class AuthorizationContractTests
{
    // ── Admin pages must NOT carry [AllowAnonymous] ────────────────────────
    // Adding [AllowAnonymous] would silently bypass auth for that page
    // without any compile-time warning.

    [Theory]
    [InlineData(typeof(AdminDashboard))]
    [InlineData(typeof(AdminMachines))]
    [InlineData(typeof(AdminSources))]
    [InlineData(typeof(AdminDocumentTriage))]
    [InlineData(typeof(AdminLinkOverrides))]
    public void AdminPage_DoesNotHaveAllowAnonymous(Type page)
    {
        Assert.Null(page.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ── Admin pages MUST require the AdminOnly policy ──────────────────────
    // Authentication alone (FallbackPolicy) is not authorization for admin
    // surfaces. Every /admin/* page carries the role-gated policy; a new
    // admin page without it fails here at authoring time.

    [Theory]
    [InlineData(typeof(AdminDashboard))]
    [InlineData(typeof(AdminMachines))]
    [InlineData(typeof(AdminSources))]
    [InlineData(typeof(AdminDocumentTriage))]
    [InlineData(typeof(AdminLinkOverrides))]
    public void AdminPage_RequiresAdminOnlyPolicy(Type page)
    {
        var authorize = page.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("AdminOnly", authorize!.Policy);
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
