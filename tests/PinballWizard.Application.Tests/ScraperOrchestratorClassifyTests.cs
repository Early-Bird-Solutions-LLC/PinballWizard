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

    // Pinball Brothers Freshdesk: "QUEEN Pinball - Rulebook" is the exact
    // article title Pinball Brothers uses — "rulebook" does not contain the
    // substring "rules", so it needs its own keyword (verified against real
    // Freshdesk content 2026-07-03).
    [Fact]
    public void ClassifyDocumentType_Rulebook_ReturnsRulesheet()
    {
        Assert.Equal(DocumentType.Rulesheet,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/1", "QUEEN Pinball - Rulebook"),
                "Freshdesk Support Portal — Queen - General"));
    }

    // Pinball Brothers Freshdesk "Electronics" folders (e.g. "QUEEN -
    // Electronics", "ALIEN - Electronics") hold schematics/wiring diagrams.
    // The folder-name context is the reliable signal — link text varies
    // per article and isn't guaranteed to say "schematic".
    [Fact]
    public void ClassifyDocumentType_ElectronicsFolderContext_ReturnsSchematic()
    {
        Assert.Equal(DocumentType.Schematic,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/2", "Alien - Schematics"),
                "Freshdesk Support Portal — ALIEN - Electronics"));
    }

    [Fact]
    public void ClassifyDocumentType_ElectronicsFolderContext_OverridesGenericLinkText()
    {
        // Even when the link text gives no hint at all, the folder-name
        // context alone must be enough to classify as Schematic.
        Assert.Equal(DocumentType.Schematic,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/3", "Wiring diagram v2"),
                "Freshdesk Support Portal — QUEEN - Electronics"));
    }

    // Both Freshdesk Service Bulletin folder-name variants ("Service
    // Bulletin" and "SERVICE BULLETINS") classify identically via the
    // existing case-insensitive "service bulletin" context substring match.
    [Theory]
    [InlineData("Freshdesk Support Portal — Service Bulletin")]
    [InlineData("Freshdesk Support Portal — SERVICE BULLETINS")]
    public void ClassifyDocumentType_FreshdeskServiceBulletinFolders_ReturnsServiceBulletin(string context)
    {
        Assert.Equal(DocumentType.ServiceBulletin,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/4", "#001 Drop target bank coil short circuit"),
                context));
    }
}
