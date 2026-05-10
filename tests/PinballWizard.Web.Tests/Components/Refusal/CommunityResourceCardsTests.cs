using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Refusal;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Refusal;

// Render-shape tests for CommunityResourceCards.
//
// These tests assert the security and anti-favoritism contracts:
//   1. All card links carry target="_blank" + rel="noopener noreferrer"
//      (security: opener isolation + referrer suppression per ADR-0026 § 5).
//   2. Cards do NOT contain "Recommended" / "Featured" / "Best" markup
//      (no favoritism markup per feedback_avoid_appearance_of_favoritism.md).
//   3. URLs render as bare hrefs — no tracking parameters are injected by
//      the component (preserves the seed URL as-is).
//   4. Empty resource list renders nothing (no empty container).
//   5. Cards render one per resource (correct count).
public sealed class CommunityResourceCardsTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Security: all card links carry target="_blank" + rel="noopener noreferrer"
    //
    // ADR-0026 § 5 — external links must carry opener isolation.
    // OWASP Reverse Tabnapping: target="_blank" without rel="noopener" lets
    // the opened page access window.opener. rel="noopener noreferrer" prevents
    // both opener access and Referer header leakage.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_AllLinks_HaveTargetBlank_And_RelNoopenerNoreferrer()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = BuildResources();

        var cut = ctx.RenderComponent<CommunityResourceCards>(p => p
            .Add(x => x.Resources, resources));

        // All links with data-testid='resource-link' must have both attributes.
        var links = cut.FindAll("[data-testid='resource-link']");
        Assert.NotEmpty(links);

        foreach (var link in links)
        {
            var target = link.GetAttribute("target");
            var rel = link.GetAttribute("rel");

            Assert.Equal("_blank", target);
            Assert.Contains("noopener", rel ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("noreferrer", rel ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Anti-favoritism: no "Recommended" / "Featured" / "Best" markup on any card.
    //
    // feedback_avoid_appearance_of_favoritism.md — umbrella principle.
    // The component must not add any superlative label that creates a visual
    // hierarchy suggesting one destination is preferred over another.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_DoesNotContain_FavoritismMarkup()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = BuildResources();

        var cut = ctx.RenderComponent<CommunityResourceCards>(p => p
            .Add(x => x.Resources, resources));

        var markup = cut.Markup;

        // Case-insensitive: the component must not inject any of these labels.
        Assert.DoesNotContain("recommended", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("featured", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("best", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top pick", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preferred", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("popular", markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // URL fidelity: URLs render as bare hrefs — no tracking params injected.
    //
    // The component must pass the seed URL through unmodified. Adding UTM
    // parameters or any other tracking query string would violate the
    // community-resource-posture ("route traffic outward, never capture").
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_URLs_AreRenderedAsBarehHrefs_NoTrackingParamsInjected()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var expectedUrl = "https://pinside.com/pinball/market";
        var resources = new List<CommunityResource>
        {
            new("Pinside Market", expectedUrl, "marketplace", "Test resource."),
        };

        var cut = ctx.RenderComponent<CommunityResourceCards>(p => p
            .Add(x => x.Resources, resources));

        // The data-href attribute mirrors the href without MudBlazor transformation.
        var link = cut.Find("[data-testid='resource-link']");
        var dataHref = link.GetAttribute("data-href");

        Assert.Equal(expectedUrl, dataHref);

        // The href rendered in the DOM must match the seed URL exactly.
        var href = link.GetAttribute("href");
        Assert.Equal(expectedUrl, href);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Render correctness: correct number of cards rendered (one per resource).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_RendersOneCardPerResource()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = BuildResources(); // 3 resources

        var cut = ctx.RenderComponent<CommunityResourceCards>(p => p
            .Add(x => x.Resources, resources));

        var cards = cut.FindAll("[data-testid='community-resource-card']");
        Assert.Equal(3, cards.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Render correctness: empty resource list renders nothing (no empty grid)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_EmptyList_RendersNothing()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<CommunityResourceCards>(p => p
            .Add(x => x.Resources, Array.Empty<CommunityResource>()));

        // No cards wrapper should render for an empty list.
        Assert.Empty(cut.FindAll("[data-testid='community-resource-cards']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Render correctness: resource name appears in the card markup.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_RenderResourceNames_InCardMarkup()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = new List<CommunityResource>
        {
            new("IPDB", "https://www.ipdb.org", "machine_reference", "Internet Pinball Machine Database."),
            new("OPDB", "https://opdb.org", "machine_reference", "Open Pinball Database."),
        };

        var cut = ctx.RenderComponent<CommunityResourceCards>(p => p
            .Add(x => x.Resources, resources));

        Assert.Contains("IPDB", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("OPDB", cut.Markup, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static List<CommunityResource> BuildResources()
    {
        return new List<CommunityResource>
        {
            new("Facebook Marketplace", "https://www.facebook.com/marketplace/category/pinball-machines",
                "marketplace", "Local pinball listings on Facebook."),
            new("Mr. Pinball", "https://mrpinball.com",
                "marketplace", "Long-running pinball classifieds."),
            new("Pinside Market", "https://pinside.com/pinball/market",
                "marketplace", "Community buy/sell section."),
        };
    }
}
