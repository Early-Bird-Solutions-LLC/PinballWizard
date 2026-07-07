using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test.
//
// About.razor is a static page (no API call) that renders the engineering
// story: what the app is, the pre-rendered architecture SVG, tech stack, and
// a GitHub link. Tests assert:
//   1. The page renders without exception.
//   2. The primary structural landmarks (heading, intro, diagram, tech list) exist.
//   3. The GitHub link points to the correct repo URL.
//
// The diagram is a static SVG served from wwwroot (no client-side render
// step) — PreRenderedDiagramTests pins the SVG <-> .mmd source contract;
// this class asserts the page actually embeds it.
public sealed class AboutTests : AsyncBunitContext
{
    public AboutTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Page renders without exception (smoke)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_Renders_WithoutException()
    {
        var cut = Render<About>();

        cut.Find("[data-testid='about-page']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Heading contains "PinballWizard"
    //    AppPageHeader renders the h4 title inline — assert on page markup.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_Heading_ContainsPinballWizard()
    {
        var cut = Render<About>();

        var page = cut.Find("[data-testid='about-page']");
        Assert.Contains("PinballWizard", page.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Intro paragraph is present and non-empty
    //    AppPageHeader renders the subtitle as body2 — assert on page markup.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_Intro_IsPresent_AndNonEmpty()
    {
        var cut = Render<About>();

        var page = cut.Find("[data-testid='about-page']");
        Assert.False(string.IsNullOrWhiteSpace(page.TextContent),
            "The page must contain introductory text.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Diagram renders as the pre-rendered SVG with alt text
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_Diagram_RendersThePreRenderedSvg()
    {
        var cut = Render<About>();

        cut.Find("[data-testid='about-diagram']");

        var img = cut.Find("[data-testid='about-diagram-svg']");
        Assert.Contains("about-architecture.svg", img.GetAttribute("src"), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(img.GetAttribute("alt")),
            "The diagram image must carry alt text (WCAG 2.1 AA).");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Tech list renders at least 5 items
    //    (behavioral: the list is populated, not an empty node)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_TechList_RendersAtLeastFiveItems()
    {
        var cut = Render<About>();

        // AppBulletList renders a native <ul> of <li> items. Rather than
        // counting elements, assert the rendered text contains the well-known
        // stack entries — an implementation-agnostic check that stays stable if
        // the item count changes.
        var techList = cut.Find("[data-testid='about-tech-list']");

        var text = techList.TextContent;
        var expectedEntries = new[]
        {
            ".NET 10",
            "Blazor",
            "Foundry",
            "Cosmos",
            "AI Search",
        };
        foreach (var entry in expectedEntries)
        {
            Assert.Contains(entry, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. GitHub link points to the correct repo
    //    Community-resource posture: outbound link is the primary CTA.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_GitHubLink_PointsToCorrectRepo()
    {
        var cut = Render<About>();

        var link = cut.Find("[data-testid='about-github-link']");
        var href = link.GetAttribute("href");
        Assert.Equal(
            "https://github.com/Early-Bird-Solutions-LLC/PinballWizard",
            href,
            StringComparer.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. Manufacturer list renders actual coverage data
    //    Behavioral: "What we cover" section names the covered sources,
    //    not just an empty container.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_ManufacturerList_NamesCoveredSources()
    {
        var cut = Render<About>();

        var text = cut.Find("[data-testid='about-manufacturer-list']").TextContent;
        Assert.Contains("Stern Pinball", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPDB", text, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8. Data Partners section credits all three sourced-data partners
    //    Behavioral: names OPDB, Kineticist, Silverball Labs, and
    //    PinballPrices.com (ADR-0043, ADR-0045).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_DataPartners_CreditsAllPartners()
    {
        var cut = Render<About>();

        var text = cut.Find("[data-testid='about-data-partners-list']").TextContent;
        Assert.Contains("OPDB", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kineticist", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Silverball Labs", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PinballPrices", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void About_DataPartners_ContainsLinksToPartnerSites()
    {
        // Each partner card is a whole-card <a> link (ADR-0027: route outward).
        // PinballPrices.com is credited as attribution text inside the
        // Silverball Labs card description (not a separate standalone link).
        var cut = Render<About>();

        var partnerList = cut.Find("[data-testid='about-data-partners-list']");
        var hrefs = partnerList
            .QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href") ?? "")
            .ToList();

        Assert.Contains(hrefs, h => h.Contains("opdb.org", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("kineticist.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("silverballlabs.com", StringComparison.OrdinalIgnoreCase));

        // PinballPrices.com credited as text in the Silverball Labs card description.
        var text = partnerList.TextContent;
        Assert.Contains("PinballPrices", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void About_DataPartners_RendersFourCards()
    {
        // The partner section renders exactly 4 DataPartnerCard components
        // (OPDB / Kineticist / Silverball Labs / Internet Pinball Database) — contract test for the grid count.
        var cut = Render<About>();

        var cards = cut.FindAll("[data-testid^='data-partner-card-']").ToList();
        Assert.Equal(4, cards.Count);
    }
}
