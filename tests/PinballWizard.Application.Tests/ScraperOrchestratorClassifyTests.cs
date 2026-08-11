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
        Assert.Equal(DocumentType.FeatureMatrix, ScraperOrchestrator.ClassifyDocumentType(Link(url, text), "Game Page → Promotional Materials tab", SourceType.GamePage));
    }

    [Fact]
    public void ClassifyDocumentType_PlainFlyer_StillFlyer()
    {
        Assert.Equal(DocumentType.Flyer, ScraperOrchestrator.ClassifyDocumentType(Link("https://x/PANTS-PRO-Flyer.pdf", "Pokémon Pro Flyer"), "Game Page → Promotional Materials tab", SourceType.GamePage));
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
            ScraperOrchestrator.ClassifyDocumentType(Link(url, linkText), "Game Page → Specs & Manual tab", SourceType.GamePage));
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
                "Game Page → Specs & Manual tab",
                SourceType.GamePage));
    }

    // Plain manual link text is unchanged.
    [Fact]
    public void ClassifyDocumentType_PlainManual_StaysManual()
    {
        Assert.Equal(DocumentType.Manual,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://x/foo-manual.pdf", "Owner's Manual"),
                "Manuals Page",
                SourceType.GamePage));
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
                "Freshdesk Support Portal — Queen - General",
                SourceType.PinballBrothersFreshdeskArticle));
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
                "Freshdesk Support Portal — ALIEN - Electronics",
                SourceType.PinballBrothersFreshdeskArticle));
    }

    [Fact]
    public void ClassifyDocumentType_ElectronicsFolderContext_OverridesGenericLinkText()
    {
        // Even when the link text gives no hint at all, the folder-name
        // context alone must be enough to classify as Schematic.
        Assert.Equal(DocumentType.Schematic,
            ScraperOrchestrator.ClassifyDocumentType(
                Link("https://pinballbrothers.freshdesk.com/helpdesk/attachments/3", "Wiring diagram v2"),
                "Freshdesk Support Portal — QUEEN - Electronics",
                SourceType.PinballBrothersFreshdeskArticle));
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
                context,
                SourceType.PinballBrothersFreshdeskArticle));
    }

    // Issue #815: AP support-page docs have source_type=ServiceBulletinPage but
    // bare link texts ("Bar Door Check", "Tank Treads Installation") and a
    // context ("American Pinball Support Page") that contain no bulletin keyword.
    // Before the fix these fell through to Other and were dropped from RAG
    // ingestion. The source type is now consulted first.
    [Fact]
    public void ClassifyDocumentType_ApBulletinPage_BareTitle_ReturnsServiceBulletin()
    {
        // Bare title with no keyword — previously classified Other.
        var link = new DiscoveredLink { FileUrl = "https://s4.american-pinball.com/img/support/2022-04/Bar-Door-Check.pdf", LinkText = "Bar Door Check" };
        Assert.Equal(DocumentType.ServiceBulletin,
            ScraperOrchestrator.ClassifyDocumentType(link, "American Pinball Support Page", SourceType.ServiceBulletinPage));
    }

    // Stern bulletins classify as ServiceBulletin even WITHOUT the
    // ServiceBulletinPage source type — context "service bulletin" fires.
    // Pin that the source-type shortcut does not regress Stern.
    [Fact]
    public void ClassifyDocumentType_SternBulletinContext_ReturnsServiceBulletin()
    {
        var link = new DiscoveredLink { FileUrl = "https://sternpinball.com/sb-godzilla-02.pdf", LinkText = "Godzilla Service Bulletin #02" };
        Assert.Equal(DocumentType.ServiceBulletin,
            ScraperOrchestrator.ClassifyDocumentType(link, "Stern Pinball service bulletin page", SourceType.GamePage));
    }

    // ManualsPage source type classifies as Manual regardless of link text,
    // making it consistent with ServiceBulletinPage behaviour.
    [Fact]
    public void ClassifyDocumentType_ManualsPageSourceType_ReturnsManual()
    {
        var link = new DiscoveredLink { FileUrl = "https://sternpinball.com/manuals/godzilla.pdf", LinkText = "Godzilla" };
        Assert.Equal(DocumentType.Manual,
            ScraperOrchestrator.ClassifyDocumentType(link, "Manuals Page", SourceType.ManualsPage));
    }

    // Mixed-content source types (e.g., SpookyPinballSupportPage) must NOT
    // get a blanket mapping — the heuristics continue to decide per-document.
    [Fact]
    public void ClassifyDocumentType_MixedSourceType_FallsThroughToHeuristics()
    {
        // SpookyPinballSupportPage has no blanket mapping; "Rules" link text → Rulesheet.
        var link = new DiscoveredLink { FileUrl = "https://spookypinball.com/wp-content/uploads/rules.pdf", LinkText = "Beetlejuice Rules" };
        Assert.Equal(DocumentType.Rulesheet,
            ScraperOrchestrator.ClassifyDocumentType(link, "Spooky Pinball Support Page", SourceType.SpookyPinballSupportPage));
    }
}
