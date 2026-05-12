using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit smoke tests for ArchitectureStoryStrip.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. Tests assert behavior (cards render, each card has a
// link) — not CSS class names or internal MudBlazor markup.
public sealed class ArchitectureStoryStripTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 1. Renders at least 3 cards
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureStoryStrip_RendersAtLeastThreeCards()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();

        var cards = cut.FindAll("[data-testid^='arch-card-']");
        Assert.True(cards.Count >= 3,
            $"ArchitectureStoryStrip must render at least 3 cards. Got {cards.Count}.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Each card contains a link to a doc or ADR
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureStoryStrip_EachCard_HasLink()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();

        var cards = cut.FindAll("[data-testid^='arch-card-']");
        Assert.True(cards.Count >= 3, "Precondition: at least 3 cards.");

        foreach (var card in cards)
        {
            var links = card.QuerySelectorAll("a[href]");
            Assert.True(links.Length >= 1,
                $"Each architecture card must contain at least one link. Card id: {card.GetAttribute("data-testid")}");

            // Each link's href must be non-empty.
            foreach (var link in links)
            {
                var href = link.GetAttribute("href");
                Assert.False(string.IsNullOrWhiteSpace(href),
                    "Architecture card link href must not be empty.");
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Strip renders without exception (smoke)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureStoryStrip_Renders_WithoutException()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();

        // data-testid on the container confirms the component mounted.
        cut.Find("[data-testid='architecture-story-strip']");
    }
}
