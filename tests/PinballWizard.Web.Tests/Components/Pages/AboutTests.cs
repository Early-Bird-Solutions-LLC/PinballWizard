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
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_Heading_ContainsPinballWizard()
    {
        var cut = Render<About>();

        var heading = cut.Find("[data-testid='about-heading']");
        Assert.Contains("PinballWizard", heading.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Intro paragraph is present and non-empty
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void About_Intro_IsPresent_AndNonEmpty()
    {
        var cut = Render<About>();

        var intro = cut.Find("[data-testid='about-intro']");
        Assert.False(string.IsNullOrWhiteSpace(intro.TextContent),
            "The intro paragraph must contain text.");
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

        // MudListItem renders divs, not li elements. Count MudListItem-based
        // cells using the mud-list-item CSS class that MudBlazor emits.
        // Fall back to checking the text content is non-empty and substantial
        // (contains multiple tech-stack entries).
        var techList = cut.Find("[data-testid='about-tech-list']");

        // MudBlazor MudListItem renders with class "mud-list-item" or similar.
        // Rather than coupling to the internal MudBlazor CSS class, assert the
        // rendered text contains at least five well-known stack entries.
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
}
