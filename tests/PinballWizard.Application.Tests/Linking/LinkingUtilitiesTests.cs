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

    private static SourceInfo SourceWith(SourceType type, string fileUrl) => new()
    {
        DiscoveryUrl = "https://example.com/",
        DiscoveryContext = "test",
        FileUrl = fileUrl,
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
            // SynthesizedArticle is the one deliberately manufacturer-agnostic source type:
            // Kineticist / Tilt Forums / TWIP articles are cross-manufacturer, persisted as
            // PlatformGeneric documents that never enter title-collision disambiguation. Its
            // null result is pinned separately below.
            if (t == SourceType.SynthesizedArticle)
            {
                continue;
            }
            data.Add(t);
        }
        return data;
    }

    // Manufacturer-hint correctness for AP bulletins (#827):
    //   ApBulletinPage   → americanpinball  (new scrapes after #827)
    //   ServiceBulletinPage + AP URL  → americanpinball  (backward compat for pre-#827 stored records)
    //   ServiceBulletinPage + non-AP URL → stern (Stern's original type is unchanged)

    [Fact]
    public void InferManufacturerKey_ApBulletinPage_YieldsAmericanPinball()
        => Assert.Equal(
            "americanpinball",
            LinkingUtilities.InferManufacturerKey(SourceWith(
                SourceType.ApBulletinPage,
                "http://s4.american-pinball.com/img/support/2024-12/Tank-Treads-Installation.pdf")));

    [Fact]
    public void InferManufacturerKey_StaleServiceBulletinPage_WithApUrl_YieldsAmericanPinball()
    {
        // Pre-#827 AP bulletins carry ServiceBulletinPage in Cosmos because issue #762
        // (re-scrape does not update scraper-owned fields) means the stored source_type
        // is never corrected. The URL-based fallback in InferManufacturerKey handles them.
        var source = SourceWith(
            SourceType.ServiceBulletinPage,
            "http://s4.american-pinball.com/img/support/2019-7/Bar-Door-Check.pdf");
        Assert.Equal("americanpinball", LinkingUtilities.InferManufacturerKey(source));
    }

    [Fact]
    public void InferManufacturerKey_ServiceBulletinPage_WithSternUrl_YieldsStern()
    {
        // Regression guard: Stern's ServiceBulletinPage documents are unaffected by the
        // AP URL-based fallback — only american-pinball.com URLs switch the hint.
        var source = SourceWith(
            SourceType.ServiceBulletinPage,
            "https://sternpinball.com/wp-content/uploads/some-bulletin.pdf");
        Assert.Equal("stern", LinkingUtilities.InferManufacturerKey(source));
    }

    // The exhaustiveness guard's inverse: SynthesizedArticle MUST have no manufacturer key,
    // because its manufacturer is per-record (DocumentRecord.Manufacturer), not inferable from
    // the source type. This pins that intent so a future change can't quietly give it one.
    [Fact]
    public void InferManufacturerKey_ReturnsNull_ForSynthesizedArticle()
        => Assert.True(
            string.IsNullOrEmpty(LinkingUtilities.InferManufacturerKey(SourceWith(SourceType.SynthesizedArticle))),
            "SynthesizedArticle is cross-manufacturer / PlatformGeneric and must not resolve to a manufacturer key.");

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
    // Colon, slash, parentheses, and exclamation marks are separators.
    [InlineData("Batman: The Dark Knight", "batman the dark knight")]
    [InlineData("AC/DC Luci", "ac dc luci")]
    [InlineData("The Avengers (Pro)", "the avengers pro")]
    [InlineData("Star Wars!", "star wars")]
    // Apostrophes are stripped.
    [InlineData("Batman '66", "batman 66")]
    [InlineData("Elvira's", "elviras")]
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
    // Punctuation titles match cleaned filenames.
    [InlineData("Batman_The_Dark_Knight_Manual.pdf", "Batman: The Dark Knight")]
    [InlineData("AC_DC_Luci_Manual.pdf", "AC/DC Luci")]
    [InlineData("The_Avengers_Pro_Manual.pdf", "The Avengers (Pro)")]
    [InlineData("Batman_66_Manual.pdf", "Batman '66")]
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
