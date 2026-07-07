using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Landing;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit tests for DataPartnersStrip.
//
// Behavioral contract (ADR-0026 §9d + ADR-0027; ADR-0050 added Tilt Forums):
//   1. Strip renders without exception (smoke).
//   2. Renders exactly four DataPartnerCard components.
//   3. Each partner (OPDB, Kineticist, Tilt Forums, Silverball Labs) has a card.
//   4. Attribution link points to /about#data-partners.
//   5. All cards are compact (landing spacing).
public sealed class DataPartnersStripTests : AsyncBunitContext
{
    public DataPartnersStripTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Renders without exception
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_Renders_WithoutException()
    {
        var cut = Render<DataPartnersStrip>();

        cut.Find("[data-testid='data-partners-strip']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Four DataPartnerCard components rendered
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_RendersExactlyFourCards()
    {
        var cut = Render<DataPartnersStrip>();

        var cards = cut.FindComponents<DataPartnerCard>();
        Assert.Equal(4, cards.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. All four partners present by name
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_ContainsAllFourPartners()
    {
        var cut = Render<DataPartnersStrip>();

        var text = cut.Find("[data-testid='data-partners-strip']").TextContent;
        Assert.Contains("OPDB",            text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kineticist",      text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tilt Forums",     text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Silverball Labs", text, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3b. Cards render in the documented Catalog → Rules → Pricing order.
    //     Ordering is load-bearing (the Partners array leads with the catalog
    //     join key) and mirrors /about#data-partners — assert position, not
    //     just presence, so an accidental reorder is caught.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_RendersCardsInCatalogRulesPricingOrder()
    {
        var cut = Render<DataPartnersStrip>();

        var names = cut.FindComponents<DataPartnerCard>()
            .Select(c => c.Instance.Name)
            .ToList();

        string[] expected = ["OPDB", "Kineticist", "Tilt Forums", "Silverball Labs"];
        Assert.Equal(expected, names);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Attribution link targets /about#data-partners
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_AttributionLink_PointsToAboutSection()
    {
        var cut = Render<DataPartnersStrip>();

        var link = cut.Find(".partners-strip__attribution-link");
        Assert.Equal("/about#data-partners", link.GetAttribute("href"), StringComparer.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. All cards are compact (landing variant)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_AllCards_AreCompact()
    {
        var cut = Render<DataPartnersStrip>();

        var cards = cut.FindComponents<DataPartnerCard>();
        foreach (var card in cards)
        {
            Assert.True(card.Instance.Compact,
                $"Card '{card.Instance.Name}' should be Compact=true on the landing strip.");
        }
    }
}
