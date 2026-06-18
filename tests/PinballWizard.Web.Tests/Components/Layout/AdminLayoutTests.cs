using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// Per ADR-0026 PR self-audit item 9(d): AdminLayout is the chrome wrapper for
// all /admin/* pages. Per the ADR-0034 amendment (2026-06-17) the drawer is
// always-open (DrawerVariant.Persistent in MudBlazor 8.x) and the hamburger
// toggle is removed — a toggle OnClick is dead on the static admin pages, and
// an always-open drawer's nav links are plain anchors that work regardless of
// each page's render mode.
//
// ADR-0008 (MudBlazor strict), ADR-0034 (admin per-need render mode).
public sealed class AdminLayoutTests : AsyncBunitContext
{
    public AdminLayoutTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
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
    public void AdminLayout_Renders_AllSixNavLinks()
    {
        var cut = RenderWithBody();

        // The six admin nav destinations must all be reachable as anchors.
        string[] hrefs =
        [
            "/admin", "/admin/sources", "/admin/machines",
            "/admin/document-triage", "/admin/link-overrides", "/admin/settings",
        ];
        foreach (var href in hrefs)
        {
            Assert.NotNull(cut.Find($"a[href='{href}']"));
        }
    }

    [Fact]
    public void AdminLayout_Drawer_IsPersistent()
    {
        var cut = RenderWithBody();

        var drawer = cut.FindComponent<MudDrawer>();
        Assert.Equal(DrawerVariant.Persistent, drawer.Instance.Variant);
        // MudBlazor 9 turned Open into a ParameterState property. The MUD0012 analyzer
        // steers component authors to GetState(x => x.Open), but GetState is protected —
        // reachable only from MudBlazor's own InternalsVisibleTo tests, not an external
        // test project. Reading the public Open parameter is functionally correct here
        // (we're asserting the value AdminLayout passes in), so the authoring analyzer
        // is suppressed for this single external-inspection assertion.
#pragma warning disable MUD0012
        Assert.True(drawer.Instance.Open, "Persistent (always-open) admin drawer must be open.");
#pragma warning restore MUD0012
    }

    [Fact]
    public void AdminLayout_HasNo_HamburgerToggle()
    {
        var cut = RenderWithBody();

        // The toggle button carried aria-label="Toggle navigation drawer".
        // A permanent drawer needs no toggle; assert it is gone so the dead-on-
        // static OnClick can't creep back.
        Assert.Empty(cut.FindAll("[aria-label='Toggle navigation drawer']"));
    }

    [Fact]
    public void AdminLayout_PassesThrough_BodyContent()
    {
        var cut = RenderWithBody();
        cut.Find("[data-testid='admin-body-sentinel']");
    }
}
