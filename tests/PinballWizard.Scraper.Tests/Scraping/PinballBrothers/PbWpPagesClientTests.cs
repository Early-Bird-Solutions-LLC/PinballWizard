using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.PinballBrothers;

/// <summary>
/// Tests for the static parsing surface of <see cref="PbWpPagesClient"/>.
/// Pinball Brothers' game-page filter is "slug ends with the
/// configured suffix (default <c>-pinball</c>)" — every shipped title
/// has followed this convention since the post-2023 site relaunch.
/// </summary>
public sealed class PbWpPagesClientTests
{
    private const string Suffix = "-pinball";

    [Fact]
    public void ParsePagesJson_BindsAllRequestedFields()
    {
        const string json = """
        [
          {
            "id": 1852,
            "slug": "queen-pinball",
            "link": "https://www.pinballbrothers.com/queen-pinball/",
            "parent": 0,
            "modified": "2026-04-01T10:00:00",
            "title": { "rendered": "Queen Pinball" }
          }
        ]
        """;

        var pages = PbWpPagesClient.ParsePagesJson(json);

        Assert.Single(pages);
        Assert.Equal(1852, pages[0].Id);
        Assert.Equal("queen-pinball", pages[0].Slug);
        Assert.Equal("https://www.pinballbrothers.com/queen-pinball/", pages[0].Link);
        Assert.Equal("Queen Pinball", pages[0].Title.Rendered);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"unrelated\":42}")]
    public void ParsePagesJson_GracefullyHandlesBadInput(string json)
    {
        Assert.Empty(PbWpPagesClient.ParsePagesJson(json));
    }

    [Fact]
    public void ParsePagesJson_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PbWpPagesClient.ParsePagesJson(null!));
    }

    [Fact]
    public void FilterGamePages_KeepsOnlyPagesWithMatchingSuffix()
    {
        var pages = new List<PbPageRaw>
        {
            new() { Id = 1, Slug = "queen-pinball", Title = new() { Rendered = "Queen Pinball" } },
            new() { Id = 2, Slug = "alien-pinball", Title = new() { Rendered = "Alien Pinball" } },
            new() { Id = 3, Slug = "abba-pinball", Title = new() { Rendered = "ABBA Pinball" } },
            new() { Id = 4, Slug = "predator-pinball", Title = new() { Rendered = "Predator Pinball" } },
            new() { Id = 5, Slug = "about-us", Title = new() { Rendered = "About Us" } },
            new() { Id = 6, Slug = "contact", Title = new() { Rendered = "Contact" } },
            new() { Id = 7, Slug = "support", Title = new() { Rendered = "Support" } },
        };

        var games = PbWpPagesClient.FilterGamePages(pages, Suffix);

        Assert.Equal(4, games.Count);
        Assert.Contains(games, p => p.Slug == "queen-pinball");
        Assert.Contains(games, p => p.Slug == "alien-pinball");
        Assert.Contains(games, p => p.Slug == "abba-pinball");
        Assert.Contains(games, p => p.Slug == "predator-pinball");
        Assert.DoesNotContain(games, p => p.Slug == "about-us");
    }

    [Fact]
    public void FilterGamePages_SuffixMatchIsCaseInsensitive()
    {
        var pages = new List<PbPageRaw>
        {
            new() { Id = 1, Slug = "Queen-PINBALL" },
        };

        var games = PbWpPagesClient.FilterGamePages(pages, Suffix);
        Assert.Single(games);
    }

    [Fact]
    public void FilterGamePages_EmptySlugsRejected()
    {
        var pages = new List<PbPageRaw>
        {
            new() { Id = 1, Slug = "" },
            new() { Id = 2, Slug = "queen-pinball" },
        };

        var games = PbWpPagesClient.FilterGamePages(pages, Suffix);
        Assert.Single(games);
    }

    [Fact]
    public void FilterGamePages_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => PbWpPagesClient.FilterGamePages(null!, Suffix));
        Assert.ThrowsAny<ArgumentException>(() => PbWpPagesClient.FilterGamePages([], "  "));
    }
}
