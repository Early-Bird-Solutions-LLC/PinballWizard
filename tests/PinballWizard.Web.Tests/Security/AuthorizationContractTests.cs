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
    [InlineData(typeof(AdminDocumentTriage))]
    [InlineData(typeof(AdminLinkOverrides))]
    [InlineData(typeof(AdminSettings))]
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
