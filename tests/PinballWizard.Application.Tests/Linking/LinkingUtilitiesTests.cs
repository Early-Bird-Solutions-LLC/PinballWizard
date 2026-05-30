using PinballWizard.Application.Linking;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

public class LinkingUtilitiesTests
{
    // NormalizeForMatch
    [Theory]
    [InlineData("stranger-things", "stranger things")]
    [InlineData("Stranger Things", "stranger things")]
    [InlineData("stranger_things", "stranger things")]
    [InlineData("TRON", "tron")]
    [InlineData("tron_legacy_manual.pdf", "tron legacy manual pdf")]
    [InlineData("", "")]
    public void NormalizeForMatch_stripsAndLowers(string input, string expected)
        => Assert.Equal(expected, LinkingUtilities.NormalizeForMatch(input));

    // IsWordBoundaryMatch — true positives
    [Theory]
    [InlineData("tron_legacy_manual.pdf", "tron")]
    [InlineData("kiss_premium_manual.pdf", "kiss")]
    [InlineData("stern_tron_le.pdf", "tron")]
    public void IsWordBoundaryMatch_matchesWholeSlug(string filename, string slug)
        => Assert.True(LinkingUtilities.IsWordBoundaryMatch(
            LinkingUtilities.NormalizeForMatch(filename),
            LinkingUtilities.NormalizeForMatch(slug)));

    // IsWordBoundaryMatch — false positive: "tron" inside "electronic"
    [Fact]
    public void IsWordBoundaryMatch_rejectsTronInElectronic()
    {
        var normFile = LinkingUtilities.NormalizeForMatch("electronic_manual.pdf");
        var normSlug = LinkingUtilities.NormalizeForMatch("tron");
        Assert.False(LinkingUtilities.IsWordBoundaryMatch(normFile, normSlug));
    }

    // ExtractEditionFromText
    [Theory]
    [InlineData("godzilla premium manual", "Premium")]
    [InlineData("metallica le rules", "LE")]
    [InlineData("mandalorian pro manual", "Pro")]
    [InlineData("batman no edition", null)]
    [InlineData("batman vault edition manual", "Vault")]
    [InlineData("batman ce manual", "CE")]
    public void ExtractEditionFromText_returnsCanonical(string text, string? expected)
        => Assert.Equal(expected, LinkingUtilities.ExtractEditionFromText(
            LinkingUtilities.NormalizeForMatch(text)));

    // ExtractEdition (slug-position anchored)
    [Theory]
    [InlineData("godzilla premium manual", "godzilla", "Premium")]
    [InlineData("godzilla pro manual", "godzilla", "Pro")]
    [InlineData("godzilla manual", "godzilla", null)]
    public void ExtractEdition_anchorsToSlugPosition(string normFilename, string normSlug, string? expected)
        => Assert.Equal(expected, LinkingUtilities.ExtractEdition(normFilename, normSlug));

    // ExtractGameSlugFromUrl
    [Theory]
    [InlineData("https://sternpinball.com/game/godzilla/", "godzilla")]
    [InlineData("https://sternpinball.com/game/stranger-things/manual/", "stranger-things")]
    [InlineData("https://sternpinball.com/manuals/", null)]
    [InlineData("", null)]
    public void ExtractGameSlugFromUrl_extractsCorrectly(string url, string? expected)
        => Assert.Equal(expected, LinkingUtilities.ExtractGameSlugFromUrl(url));
}
