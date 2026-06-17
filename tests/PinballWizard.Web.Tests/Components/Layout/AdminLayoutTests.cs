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
// PERMANENT (always visible) and the hamburger toggle is removed — a toggle
// OnClick is dead on the static admin pages, and a permanent drawer's nav links
// are plain anchors that work regardless of each page's render mode.
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
    public void AdminLayout_Drawer_IsPermanent()
    {
        var cut = RenderWithBody();

        var drawer = cut.FindComponent<MudDrawer>();
        Assert.Equal(DrawerVariant.Persistent, drawer.Instance.Variant);
        Assert.True(drawer.Instance.Open, "Persistent (always-open) admin drawer must be open.");
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
