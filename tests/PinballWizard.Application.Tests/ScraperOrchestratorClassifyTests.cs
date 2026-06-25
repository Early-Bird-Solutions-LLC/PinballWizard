using PinballWizard.Application;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using Xunit;

namespace PinballWizard.Application.Tests;

public sealed class ScraperOrchestratorClassifyTests
{
    private static DiscoveredLink Link(string url, string? text) =>
        new() { FileUrl = url, LinkText = text, DiscoveryContext = "Game Page → Promotional Materials tab", GameSlug = "pokemon" };

    [Theory]
    [InlineData("https://sternpinball.com/wp-content/uploads/2026/02/PANTS-Matrix.pdf", "Pokémon by Stern Pinball Feature Matrix")]
    [InlineData("https://sternpinball.com/x/matrix.pdf", "Game Feature Matrix")]
    public void ClassifyDocumentType_FeatureMatrix_Detected(string url, string text)
    {
        Assert.Equal(DocumentType.FeatureMatrix, ScraperOrchestrator.ClassifyDocumentType(Link(url, text), "Game Page → Promotional Materials tab"));
    }

    [Fact]
    public void ClassifyDocumentType_PlainFlyer_StillFlyer()
    {
        Assert.Equal(DocumentType.Flyer, ScraperOrchestrator.ClassifyDocumentType(Link("https://x/PANTS-PRO-Flyer.pdf", "Pokémon Pro Flyer"), "Game Page → Promotional Materials tab"));
    }

    // ADR-0042: standalone rules PDFs (previously falling to Other) must now
    // classify as Rulesheet so they are admitted by the RAG allow-list.
    [Theory]
    [InlineData("https://spookypinball.com/rules/spooky-rules.pdf", "Rules")]
    [InlineData("https://chicago-gaming.com/docs/afm-rules.pdf", "Spooky Rules")]
    [InlineData("https://american-pinball.com/downloads/game-rulesheet.pdf", "Rulesheet")]
    [InlineData("https://example.com/beetlejuice-rule-sheet.pdf", "Rule Sheet")]
    [InlineData("https://cgc.com/docs/rules.pdf", null)]
    public void ClassifyDocumentType_RulesPdf_ReturnsRulesheet(string url, string? linkText)
    {
        Assert.Equal(DocumentType.Rulesheet,
            ScraperOrchestrator.ClassifyDocumentType(Link(url, linkText), "Game Page → Specs & Manual tab"));
    }

    // ADR-0042: "Rules Manual" (link text contains both "rules" and "manual")
    // must stay Manual — the manual branch fires first since it appears before
    // the rules branch in ClassifyDocumentType.
    [Fact]
    public void ClassifyDocumentType_RulesManual_StaysManual()
    {
        Assert.Equal(DocumentType.Manual,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://x/rules-manual.pdf", "Rules Manual"),
                "Game Page → Specs & Manual tab"));
    }

    // Plain manual link text is unchanged.
    [Fact]
    public void ClassifyDocumentType_PlainManual_StaysManual()
    {
        Assert.Equal(DocumentType.Manual,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://x/foo-manual.pdf", "Owner's Manual"),
                "Manuals Page"));
    }
}
