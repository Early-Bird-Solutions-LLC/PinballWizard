using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

// bUnit tests for DataPartnerCard.
//
// Behavioral contract (ADR-0026 PR audit §9d: every Razor component needs
// a bUnit smoke test):
//   1. Whole card renders as <a> pointing to Href — single hitbox for a11y.
//   2. Role eyebrow text is rendered (ADR-0027 partner attribution).
//   3. Name text is rendered (partner identity).
//   4. Status dot / text is rendered (active-partnership signal).
//   5. Compact vs. roomy modifier class applied correctly.
//   6. data-testid derived from Role (lowercase) for E2E targeting.
public sealed class DataPartnerCardTests : AsyncBunitContext
{
    public DataPartnerCardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<DataPartnerCard> RenderCard(bool compact = true) =>
        Render<DataPartnerCard>(p => p
            .Add(x => x.Role,        "Catalog")
            .Add(x => x.Name,        "OPDB")
            .Add(x => x.Href,        "https://opdb.org")
            .Add(x => x.Description, "The canonical machine catalog.")
            .Add(x => x.StatusText,  "Canonical · active")
            .Add(x => x.Compact,     compact));

    // ──────────────────────────────────────────────────────────────────────
    // 1. Whole card is an <a> link (ADR-0027: route outward, single hitbox)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_IsAnchorElement_WithCorrectHref()
    {
        var cut = RenderCard();

        var anchor = cut.Find("a");
        Assert.Equal("https://opdb.org", anchor.GetAttribute("href"), StringComparer.Ordinal);
    }

    [Fact]
    public void DataPartnerCard_Anchor_OpensInNewTab()
    {
        var cut = RenderCard();

        var anchor = cut.Find("a");
        Assert.Equal("_blank", anchor.GetAttribute("target"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("noopener", anchor.GetAttribute("rel") ?? "", StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Role eyebrow renders (attribution category label)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_RendersRoleEyebrow()
    {
        var cut = RenderCard();

        var role = cut.Find(".partner-card__role");
        Assert.Contains("Catalog", role.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Name renders with arrow glyph
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_RendersName()
    {
        var cut = RenderCard();

        var name = cut.Find(".partner-card__name");
        Assert.Contains("OPDB", name.TextContent, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Status chip renders
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_RendersStatusText()
    {
        var cut = RenderCard();

        var status = cut.Find(".partner-card__status");
        Assert.Contains("Canonical", status.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5a. Compact=true applies base class only (landing)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_Compact_AppliesBaseClassOnly()
    {
        var cut = RenderCard(compact: true);

        var anchor = cut.Find("a");
        var cls = anchor.GetAttribute("class") ?? string.Empty;
        Assert.Contains("partner-card", cls, StringComparison.Ordinal);
        Assert.DoesNotContain("partner-card--roomy", cls, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5b. Compact=false adds --roomy modifier (About page)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_NotCompact_AppliesRoomyModifier()
    {
        var cut = RenderCard(compact: false);

        var anchor = cut.Find("a");
        var cls = anchor.GetAttribute("class") ?? string.Empty;
        Assert.Contains("partner-card--roomy", cls, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. data-testid is derived from Role (lowercase)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataPartnerCard_TestId_IsLowercasedRole()
    {
        var cut = RenderCard();

        cut.Find("[data-testid='data-partner-card-catalog']");
    }
}
