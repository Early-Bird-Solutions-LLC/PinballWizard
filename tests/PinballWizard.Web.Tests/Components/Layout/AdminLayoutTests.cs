using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// Per ADR-0026 PR self-audit item 9(d): AdminLayout is the chrome wrapper for
// all /admin/* pages. Navigation is the shared AppNavRail in static always-
// expanded mode (ShowToggle="false", no @rendermode): always visible, anchor-
// only nav links that work regardless of each page's render mode, and no toggle
// (a toggle OnClick would be dead on the static admin pages). This keeps the
// rail's interactivity profile identical to the previous inline drawer.
//
// AdminLayout now contains AuthorizeView, so all render paths need AddAuthorization().
// Anonymous baseline (NotAuthorized branch) is the default state used by the
// structural tests below; banner-specific tests use their own context classes.
//
// ADR-0008 (MudBlazor strict), ADR-0034 (admin per-need render mode).
public sealed class AdminLayoutTests : AsyncBunitContext
{
    public AdminLayoutTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        // AuthorizeView requires IAuthorizationPolicyProvider; anonymous state
        // is the baseline (structural tests don't care which branch renders).
        this.AddAuthorization();
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    private IRenderedComponent<AdminLayout> RenderWithBody() =>
        Render<AdminLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "data-testid", "admin-body-sentinel");
                builder.AddContent(2, "Body content");
                builder.CloseElement();
            }));

    [Fact]
    public void AdminLayout_Renders_AllCoreNavLinks()
    {
        var cut = RenderWithBody();

        // All ten admin nav destinations must be reachable as anchors.
        string[] hrefs =
        [
            "/admin", "/admin/sources", "/admin/manufacturers", "/admin/machines", "/admin/documents",
            "/admin/document-triage", "/admin/link-overrides", "/admin/jobs",
            "/admin/monitoring", "/admin/settings",
        ];
        foreach (var href in hrefs)
        {
            Assert.NotNull(cut.Find($"a[href='{href}']"));
        }
    }

    [Fact]
    public void AdminLayout_NavRail_IsOpenAndMini()
    {
        var cut = RenderWithBody();

        // Admin hosts the shared AppNavRail expanded on load (Open="true").
        var drawer = cut.FindComponent<MudDrawer>();
        Assert.Equal(DrawerVariant.Mini, drawer.Instance.Variant);
        // MudBlazor 9 turned Open into a ParameterState property. The MUD0012 analyzer
        // steers component authors to GetState(x => x.Open), but GetState is protected —
        // reachable only from MudBlazor's own InternalsVisibleTo tests, not an external
        // test project. Reading the public Open parameter is functionally correct here
        // (we're asserting the value AdminLayout passes in), so the authoring analyzer
        // is suppressed for this single external-inspection assertion.
#pragma warning disable MUD0012
        Assert.True(drawer.Instance.Open, "Admin nav rail must be expanded on load.");
#pragma warning restore MUD0012
    }

    [Fact]
    public void AdminLayout_NavRail_HasNoToggle()
    {
        var cut = RenderWithBody();

        // Admin hosts AppNavRail with ShowToggle="false" — a static always-expanded
        // rail with no toggle button (a toggle OnClick would be dead on the static
        // admin pages, the original hamburger bug). Assert neither the current rail
        // toggle nor the old hamburger aria-label is present.
        Assert.Empty(cut.FindAll("[data-testid='nav-rail-toggle']"));
        Assert.Empty(cut.FindAll("[aria-label='Toggle navigation drawer']"));
    }

    [Fact]
    public void AdminLayout_NavRail_DoesNotOptIntoInteractiveFeatures()
    {
        var cut = RenderWithBody();

        // Guard: AdminLayout passes none of the opt-in enhancement params.
        // HoverToPeek, Persist, and ShowToggle must all be false — the admin rail
        // is a static always-expanded rail that is deliberately outside the
        // interactive feature set added to the public MainLayout rail.
        var rail = cut.FindComponent<AppNavRail>();
        Assert.False(rail.Instance.HoverToPeek);
        Assert.False(rail.Instance.Persist);
        Assert.False(rail.Instance.ShowToggle);
    }

    [Fact]
    public void AdminLayout_PassesThrough_BodyContent()
    {
        var cut = RenderWithBody();
        cut.Find("[data-testid='admin-body-sentinel']");
    }

    [Fact]
    public void AdminNav_IncludesManufacturersLink()
    {
        var cut = RenderWithBody();

        cut.Find("a[href='/admin/manufacturers']");
    }

    // ── Anonymous path — identity block must not appear ────────────────────
    // Unauthenticated users are redirected by [Authorize] on each page before
    // the layout renders in practice; the AuthorizeView here is a belt-and-
    // suspenders guard that hides the identity line when auth state is absent.

    [Fact]
    public void AdminLayout_AnonymousUser_DoesNotRenderIdentityBlock()
    {
        var cut = RenderWithBody();

        Assert.Empty(cut.FindAll("[data-testid='admin-identity']"));
    }
}

// Separate context for the authorized-admin branch so SetAuthorized is
// registered before the service provider is locked.
public sealed class AdminLayoutAuthorizedTests : AsyncBunitContext
{
    public AdminLayoutAuthorizedTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization()
            .SetAuthorized("test-admin@example.com");
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    private IRenderedComponent<AdminLayout> RenderWithBody() =>
        Render<AdminLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "data-testid", "admin-body-sentinel");
                builder.AddContent(2, "Body content");
                builder.CloseElement();
            }));

    [Fact]
    public void AdminLayout_AuthorizedAdmin_RendersIdentityAndSignOut()
    {
        var cut = RenderWithBody();

        var identity = cut.Find("[data-testid='admin-identity']");
        Assert.Contains("test-admin@example.com", identity.TextContent, StringComparison.Ordinal);
        cut.Find("a[href='/MicrosoftIdentity/Account/SignOut']");
    }

    [Fact]
    public void AdminLayout_AuthorizedAdmin_DoesNotRenderSignInLink()
    {
        var cut = RenderWithBody();

        Assert.Empty(cut.FindAll("a[href='/MicrosoftIdentity/Account/SignIn']"));
    }
}
