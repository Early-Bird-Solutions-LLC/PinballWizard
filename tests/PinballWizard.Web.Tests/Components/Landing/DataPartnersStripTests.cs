using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Landing;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit tests for DataPartnersStrip.
//
// Behavioral contract (ADR-0026 §9d + ADR-0027):
//   1. Strip renders without exception (smoke).
//   2. Renders exactly three DataPartnerCard components.
//   3. Each partner (OPDB, Kineticist, Silverball Labs) has a card.
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
    // 2. Three DataPartnerCard components rendered
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_RendersExactlyThreeCards()
    {
        var cut = Render<DataPartnersStrip>();

        var cards = cut.FindComponents<DataPartnerCard>();
        Assert.Equal(3, cards.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. All three partners present by name
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnersStrip_ContainsAllThreePartners()
    {
        var cut = Render<DataPartnersStrip>();

        var text = cut.Find("[data-testid='data-partners-strip']").TextContent;
        Assert.Contains("OPDB",           text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kineticist",     text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Silverball Labs", text, StringComparison.OrdinalIgnoreCase);
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
