using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Theming;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Theming;

// Pins the chrome nav structure to docs/ui/screens/answer-with-citations.md
// § Screen zones #1: "Brand mark on the left, 'What we cover' link on
// the right." Mechanically prevents nav-link inflation regression — the
// audit at docs/PHASE5-DRIFT-AUDIT.md § 3 caught this drift on Wave 1
// (4-link nav read as generic SaaS); without a structural test the next
// "let's add a link" edit will quietly re-introduce the same drift.
public sealed class BrandHeaderTests : AsyncBunitContext
{
    public BrandHeaderTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void BrandHeader_RendersExactlyFourAnchors_BrandWhatWeCoverDocumentsAndBehindTheScenes()
    {
        var cut = Render<BrandHeader>();

        var anchors = cut.FindAll("a");
        Assert.Equal(4, anchors.Count);
    }

    [Fact]
    public void BrandHeader_RendersBehindTheScenesLink()
    {
        var cut = Render<BrandHeader>();

        var link = cut.Find("a[href='/admin']");
        Assert.Contains("Behind the Scenes", link.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void BrandHeader_RendersDocumentsLink()
    {
        var cut = Render<BrandHeader>();

        var link = cut.Find("a[href='/documents']");
        Assert.Contains("Documents", link.TextContent, StringComparison.Ordinal);
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
    public void BrandHeader_NavLink_LinksToAbout_WithWhatWeCoverLabel()
    {
        var cut = Render<BrandHeader>();

        // The "What we cover" nav link sits first inside the <nav aria-label="Main navigation"> region.
        var navAnchor = cut.Find("nav[aria-label='Main navigation'] a");
        Assert.Equal("/about", navAnchor.GetAttribute("href"));
        Assert.Contains("What we cover", navAnchor.TextContent);
    }

    [Fact]
    public void BrandHeader_DoesNotLinkTo_RemovedRoutes()
    {
        // Drift guard: Home / /wizard / /status used to be exposed as nav links.
        // The audit moved /status to the footer (handled by BrandFooter) and
        // dropped Home + Wizard as redundant. Mechanically pin the absence so
        // a future "let's add Status back to the header" edit fails this test.
        var cut = Render<BrandHeader>();

        var hrefs = cut.FindAll("a")
            .Select(a => a.GetAttribute("href"))
            .ToArray();

        Assert.DoesNotContain("/wizard", hrefs);
        Assert.DoesNotContain("/status", hrefs);
        // "/" is allowed (brand mark) — only assert the redundant nav-Home is gone
        // by counting anchors (covered by BrandHeader_RendersExactlyFourAnchors...).
    }
}
