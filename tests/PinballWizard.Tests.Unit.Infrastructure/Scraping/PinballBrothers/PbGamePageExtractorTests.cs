using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Tests for <see cref="PbGamePageExtractor"/>. Pinball Brothers'
/// game pages come from the WP REST API as JSON; the extractor's job
/// is to (1) reject pages whose slug does not end in the configured
/// suffix, (2) strip the suffix to derive the canonical slug, (3)
/// HTML-decode the title, (4) build a <c>game_pinballbrothers_*</c>
/// GameId so the reconciler routes to the right partition.
/// </summary>
public sealed class PbGamePageExtractorTests
{
    private const string Suffix = "-pinball";

    [Fact]
    public void ExtractGame_BuildsRecordWithCanonicalSlug()
    {
        var page = new PbPageRaw
        {
            Id = 1852,
            Slug = "queen-pinball",
            Link = "https://www.pinballbrothers.com/queen-pinball/",
            Title = new() { Rendered = "Queen Pinball" },
        };

        var record = PbGamePageExtractor.ExtractGame(page, Suffix);

        Assert.NotNull(record);
        Assert.Equal("Queen Pinball", record!.Title);
        Assert.Equal("queen", record.Slug);
        Assert.Equal("game_pinballbrothers_queen", record.GameId);
        Assert.Equal("https://www.pinballbrothers.com/queen-pinball/", record.GamePageUrl);
        Assert.Equal(["pinballbrothers_wp_pages"], record.DiscoveredOn);
        Assert.NotNull(record.Source);
        Assert.Equal("https://www.pinballbrothers.com/queen-pinball/", record.Source!.ScrapedFrom);
    }

    [Theory]
    [InlineData("queen-pinball", "queen")]
    [InlineData("alien-pinball", "alien")]
    [InlineData("abba-pinball", "abba")]
    [InlineData("predator-pinball", "predator")]
    public void ExtractGame_StripsSuffixFromSlug(string wpSlug, string expectedCanonical)
    {
        var page = new PbPageRaw
        {
            Slug = wpSlug,
            Link = $"https://www.pinballbrothers.com/{wpSlug}/",
            Title = new() { Rendered = "Game" },
        };

        var record = PbGamePageExtractor.ExtractGame(page, Suffix);
        Assert.NotNull(record);
        Assert.Equal(expectedCanonical, record!.Slug);
    }

    [Fact]
    public void ExtractGame_HtmlEntitiesInTitle_AreDecoded()
    {
        var page = new PbPageRaw
        {
            Slug = "queen-pinball",
            Link = "https://www.pinballbrothers.com/queen-pinball/",
            Title = new() { Rendered = "Queen &#8211; Bohemian Rhapsody" },
        };

        var record = PbGamePageExtractor.ExtractGame(page, Suffix);
        Assert.NotNull(record);
        Assert.Equal("Queen – Bohemian Rhapsody", record!.Title);
    }

    [Fact]
    public void ExtractGame_NonGameSlug_ReturnsNull()
    {
        var page = new PbPageRaw
        {
            Slug = "about-us",
            Link = "https://www.pinballbrothers.com/about-us/",
            Title = new() { Rendered = "About" },
        };

        Assert.Null(PbGamePageExtractor.ExtractGame(page, Suffix));
    }

    [Fact]
    public void ExtractGame_SlugEqualsSuffixOnly_ReturnsNull()
    {
        // After stripping, the canonical slug would be empty — reject.
        var page = new PbPageRaw
        {
            Slug = "-pinball",
            Link = "https://www.pinballbrothers.com/-pinball/",
            Title = new() { Rendered = "X" },
        };

        Assert.Null(PbGamePageExtractor.ExtractGame(page, Suffix));
    }

    [Fact]
    public void ExtractGame_EmptyTitle_ReturnsNull()
    {
        var page = new PbPageRaw
        {
            Slug = "queen-pinball",
            Link = "https://www.pinballbrothers.com/queen-pinball/",
            Title = new() { Rendered = "  " },
        };

        Assert.Null(PbGamePageExtractor.ExtractGame(page, Suffix));
    }

    [Fact]
    public void ExtractGame_EmptyLink_ReturnsNull()
    {
        var page = new PbPageRaw
        {
            Slug = "queen-pinball",
            Link = "",
            Title = new() { Rendered = "Queen" },
        };

        Assert.Null(PbGamePageExtractor.ExtractGame(page, Suffix));
    }

    [Theory]
    [InlineData("queen-pinball", "-pinball", "queen")]
    [InlineData("queen-PINBALL", "-pinball", "queen")] // case-insensitive
    [InlineData("queen-flipper", "-pinball", "queen-flipper")] // not a game
    public void StripSuffix_CaseInsensitive(string slug, string suffix, string expected)
    {
        Assert.Equal(expected, PbGamePageExtractor.StripSuffix(slug, suffix));
    }

    [Fact]
    public void ExtractGame_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => PbGamePageExtractor.ExtractGame(null!, Suffix));
        Assert.ThrowsAny<ArgumentException>(() => PbGamePageExtractor.ExtractGame(new PbPageRaw(), "  "));
    }

    [Fact]
    public void StripSuffix_NullArgsThrow()
    {
        Assert.ThrowsAny<ArgumentException>(() => PbGamePageExtractor.StripSuffix(null!, Suffix));
        Assert.ThrowsAny<ArgumentException>(() => PbGamePageExtractor.StripSuffix("queen-pinball", null!));
    }
}
