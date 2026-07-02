using System.Linq;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Theming;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Theming;

// Pins the header as brand-mark-only after nav links moved to AppNavRail
// (design doc: docs/superpowers/specs/2026-07-01-public-left-nav-design.md).
// Mechanically prevents duplicate-nav regression — a future edit that re-adds
// nav links to the header will fail BrandHeader_RendersExactlyOneAnchor_BrandMarkOnly
// and BrandHeader_DoesNotRender_MovedNavLinks before it ever ships.
public sealed class BrandHeaderTests : AsyncBunitContext
{
    public BrandHeaderTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void BrandHeader_BrandMark_LinksToRoot()
    {
        var cut = Render<BrandHeader>();

        var brandLink = cut.Find("a.brand-logo");
        Assert.Equal("/", brandLink.GetAttribute("href"));
        Assert.Contains("PinballWizard", brandLink.TextContent);
        // Accessibility floor — the brand mark is the screen-reader-discoverable
        // home link. Per ADR-0026 § 9(d), bUnit chrome tests substitute for axe-core
        // until that integration ships; pin the aria-label so a future copy edit
        // (or removal) fails the test rather than silently regressing AT users.
        Assert.Equal("PinballWizard home", brandLink.GetAttribute("aria-label"));
    }

    [Fact]
    public void BrandHeader_RendersExactlyOneAnchor_BrandMarkOnly()
    {
        // Nav links moved into AppNavRail (design 2026-07-01). The header is now
        // brand-mark-only; pin that so a future edit can't re-add duplicate header nav.
        var cut = Render<BrandHeader>();
        Assert.Single(cut.FindAll("a"));
    }

    [Fact]
    public void BrandHeader_DoesNotRender_MovedNavLinks()
    {
        var cut = Render<BrandHeader>();
        var hrefs = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToArray();
        Assert.DoesNotContain("/about", hrefs);
        Assert.DoesNotContain("/documents", hrefs);
        Assert.DoesNotContain("/admin", hrefs);
        Assert.DoesNotContain("/wizard", hrefs);
        Assert.DoesNotContain("/status", hrefs);
    }
}
