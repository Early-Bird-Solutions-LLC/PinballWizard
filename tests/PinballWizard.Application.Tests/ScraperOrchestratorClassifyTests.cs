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
}
