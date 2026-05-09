using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// Pins the footer's coverage-transparency surface to the locked copy in
// docs/ui/screens/empty-landing.md § Section 4 and the outbound-link set
// per docs/PHASE5-DRIFT-AUDIT.md § 2. The footer is the prospect's last
// chance to leave the Wizard for source / community on every screen
// (ADR-0027 § 4 coverage-transparency posture). Without this test the
// next "let's tighten the footer copy" edit can quietly drop the
// coverage statement, the GitHub link, or the relocated Status link.
public sealed class BrandFooterTests : TestContext
{
    public BrandFooterTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<FakeNavigationManager>();
    }

    [Fact]
    public void BrandFooter_RendersFooterLandmark_WithLabel()
    {
        var cut = RenderComponent<BrandFooter>();

        var footer = cut.Find("footer.brand-footer");
        Assert.Equal("Site footer", footer.GetAttribute("aria-label"));
    }

    [Fact]
    public void BrandFooter_RendersLockedCoverageStatement()
    {
        var cut = RenderComponent<BrandFooter>();

        var coverage = cut.Find(".footer-coverage").TextContent;
        // Copy locked from docs/ui/screens/empty-landing.md § Section 4.
        Assert.Contains("first-party data on 8 active manufacturers and OPDB", coverage);
        Assert.Contains("Everything else routes to community resources", coverage);
    }

    [Fact]
    public void BrandFooter_WhatWeCoverLink_HrefsAbout()
    {
        var cut = RenderComponent<BrandFooter>();

        var link = cut.Find("a.footer-coverage-link");
        Assert.Equal("/about", link.GetAttribute("href"));
        Assert.Contains("What we cover", link.TextContent);
        Assert.Equal("What we cover", link.GetAttribute("aria-label"));
    }

    [Fact]
    public void BrandFooter_GitHubLink_PointsToRepo_AndOpensInNewTab_Safely()
    {
        var cut = RenderComponent<BrandFooter>();

        var link = cut.Find("a.footer-github-link");
        Assert.Equal(
            "https://github.com/Early-Bird-Solutions-LLC/PinballWizard",
            link.GetAttribute("href"));
        // External link must open in new tab WITH rel="noopener noreferrer" —
        // standard tabnabbing protection. The audit doesn't call this out but
        // the showcase posture's "would a sceptical prospect lose confidence"
        // test catches a missing rel here.
        Assert.Equal("_blank", link.GetAttribute("target"));
        var rel = link.GetAttribute("rel") ?? string.Empty;
        Assert.Contains("noopener", rel);
        Assert.Contains("noreferrer", rel);
    }

    [Fact]
    public void BrandFooter_StatusLink_RelocatedFromHeader_HrefsStatus()
    {
        var cut = RenderComponent<BrandFooter>();

        var link = cut.Find("a.footer-status-link");
        Assert.Equal("/status", link.GetAttribute("href"));
        Assert.Equal("System status", link.GetAttribute("aria-label"));
    }
}
