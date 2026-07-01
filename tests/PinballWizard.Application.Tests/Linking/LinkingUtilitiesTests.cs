using PinballWizard.Application.Linking;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

public class LinkingUtilitiesTests
{
    private static SourceInfo SourceWith(SourceType type) => new()
    {
        DiscoveryUrl = "https://example.com/",
        DiscoveryContext = "test",
        FileUrl = "https://example.com/x.pdf",
        ScrapedAt = DateTime.UtcNow,
        SourceType = type,
    };

    // Exhaustiveness guard: EVERY SourceType maps to a manufacturer key. Each of
    // the current values is a manufacturer-specific scraper page, so all must
    // resolve — a future scraper that adds a SourceType without updating
    // InferManufacturerKey would silently fall to the un-disambiguated path and
    // could reintroduce the title-collision mislabel. This test fails loudly
    // (rather than that bug recurring undetected) when that happens.
    [Theory]
    [MemberData(nameof(AllSourceTypes))]
    public void InferManufacturerKey_MapsEverySourceType(SourceType type)
        => Assert.False(
            string.IsNullOrEmpty(LinkingUtilities.InferManufacturerKey(SourceWith(type))),
            $"SourceType.{type} has no manufacturer key — update InferManufacturerKey when adding a scraper.");

    public static TheoryData<SourceType> AllSourceTypes()
    {
        var data = new TheoryData<SourceType>();
        foreach (var t in Enum.GetValues<SourceType>())
        {
            data.Add(t);
        }
        return data;
    }

    // NormalizeForMatch
    [Theory]
    [InlineData("stranger-things", "stranger things")]
    [InlineData("Stranger Things", "stranger things")]
    [InlineData("stranger_things", "stranger things")]
    [InlineData("TRON", "tron")]
    [InlineData("tron_legacy_manual.pdf", "tron legacy manual pdf")]
    [InlineData("", "")]
    // camelCase / acronym / letter-digit boundaries: concatenated filename
    // titles must tokenize like separator-delimited slugs (bug 1a).
    [InlineData("JamesBond007", "james bond 007")]
    [InlineData("StarWars", "star wars")]
    [InlineData("JurassicPark", "jurassic park")]
    [InlineData("TMNT", "tmnt")]                       // all-caps acronym: no split
    [InlineData("TMNTGame", "tmnt game")]              // acronym→word boundary
    [InlineData("JamesBond007_Pro_web.pdf", "james bond 007 pro web pdf")]
    // Ampersand (and other non-alphanumeric punctuation) is a separator, so a
    // title with '&' normalizes the same as its hyphenated slug — e.g. the Stern
    // "Dungeons & Dragons" slug 'dungeons-dragons' must match page text
    // "Dungeons & Dragons" (corpus-mislink: the Stern D&D manual was landing on
    // the classic Bally D&D because '&' text never matched the '-' slug).
    [InlineData("Dungeons & Dragons", "dungeons dragons")]
    [InlineData("dungeons-dragons", "dungeons dragons")]
    public void NormalizeForMatch_stripsAndLowers(string input, string expected)
        => Assert.Equal(expected, LinkingUtilities.NormalizeForMatch(input));

    // IsWordBoundaryMatch — true positives
    [Theory]
    [InlineData("tron_legacy_manual.pdf", "tron")]
    [InlineData("kiss_premium_manual.pdf", "kiss")]
    [InlineData("stern_tron_le.pdf", "tron")]
    // camelCase-concatenated filename title now matches the separator slug (bug 1a).
    [InlineData("JamesBond007_Pro_web.pdf", "james-bond-007")]
    [InlineData("JurassicPark_Pro_web.pdf", "jurassic-park")]
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
