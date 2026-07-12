using PinballWizard.Infrastructure.Scraping.Spooky;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Spooky;

/// <summary>
/// Tests for the static parsing surface of <see cref="SpookyWpPagesClient"/>.
/// The client itself reads paginated WP REST responses; the parsing
/// concerns we lock in here are: (1) array deserialization round-trip,
/// (2) the single-S3-slug game-page filter that distinguishes a real
/// game from an aggregator/update page.
/// </summary>
public sealed class SpookyWpPagesClientTests
{
    private const string S3Host = "spookypinball.s3.us-east-2.amazonaws.com";

    [Fact]
    public void ParsePagesJson_BindsAllRequestedFields()
    {
        const string json = """
        [
          {
            "id": 6438,
            "slug": "beetlejuice",
            "link": "https://www.spookypinball.com/beetlejuice/",
            "parent": 0,
            "modified": "2026-04-10T23:25:43",
            "title": { "rendered": "Beetlejuice" },
            "content": { "rendered": "<p>code page</p>" }
          }
        ]
        """;

        var pages = SpookyWpPagesClient.ParsePagesJson(json);

        Assert.Single(pages);
        var page = pages[0];
        Assert.Equal(6438, page.Id);
        Assert.Equal("beetlejuice", page.Slug);
        Assert.Equal("https://www.spookypinball.com/beetlejuice/", page.Link);
        Assert.Equal(0, page.Parent);
        Assert.Equal("2026-04-10T23:25:43", page.Modified);
        Assert.Equal("Beetlejuice", page.Title.Rendered);
        Assert.Equal("<p>code page</p>", page.Content.Rendered);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("not-json-at-all")]
    [InlineData("{\"not\":\"an array\"}")]
    public void ParsePagesJson_GracefullyHandlesNonArrayBodies(string json)
    {
        var pages = SpookyWpPagesClient.ParsePagesJson(json);
        Assert.Empty(pages);
    }

    [Fact]
    public void ParsePagesJson_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => SpookyWpPagesClient.ParsePagesJson(null!));
    }

    [Fact]
    public void ExtractS3Slugs_ReturnsDistinctFirstPathSegments()
    {
        const string html = """
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/software_versions/release/v1.beetlejuice">v1</a>
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/software_versions/release/v2.beetlejuice">v2</a>
            """;

        var slugs = SpookyWpPagesClient.ExtractS3Slugs(html, S3Host);

        Assert.Single(slugs);
        Assert.Contains("beetlejuice", slugs);
    }

    [Fact]
    public void ExtractS3Slugs_DistinguishesAggregatorPages()
    {
        // Mimics the real "SCOOBY BASE IMAGE UPDATE" page that links to firmware
        // for several different games — should NOT be treated as a single game.
        const string html = """
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/scooby/foo">Scooby</a>
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/foo">BJ</a>
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/texaschainsaw/foo">TCM</a>
            """;

        var slugs = SpookyWpPagesClient.ExtractS3Slugs(html, S3Host);

        Assert.Equal(3, slugs.Count);
    }

    [Fact]
    public void ExtractS3Slugs_IgnoresNonS3Urls()
    {
        const string html = """
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/foo">Halloween</a>
            <a href="https://example.com/halloween/random">Other</a>
            <a href="/local/halloween/x">Local</a>
            """;

        var slugs = SpookyWpPagesClient.ExtractS3Slugs(html, S3Host);

        Assert.Single(slugs);
        Assert.Contains("halloween", slugs);
    }

    [Fact]
    public void ExtractS3Slugs_EmptyHtmlReturnsEmptySet()
    {
        var slugs = SpookyWpPagesClient.ExtractS3Slugs(string.Empty, S3Host);
        Assert.Empty(slugs);
    }

    [Fact]
    public void ExtractS3Slugs_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => SpookyWpPagesClient.ExtractS3Slugs(null!, S3Host));
        Assert.ThrowsAny<ArgumentException>(() => SpookyWpPagesClient.ExtractS3Slugs("html", "  "));
    }

    [Fact]
    public void FilterGamePages_KeepsSingleSlugPagesOnly()
    {
        var pages = new List<SpookyPageRaw>
        {
            new() // game: single slug
            {
                Id = 1, Slug = "beetlejuice",
                Link = "https://www.spookypinball.com/beetlejuice/",
                Title = new() { Rendered = "Beetlejuice" },
                Content = new() { Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/software_versions/release/v1.beetlejuice">v1</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/software_versions/release/v2.beetlejuice">v2</a>
                """ },
            },
            new() // aggregator: 3 slugs
            {
                Id = 2, Slug = "2445-2",
                Link = "https://www.spookypinball.com/2445-2/",
                Title = new() { Rendered = "SCOOBY BASE IMAGE UPDATE" },
                Content = new() { Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/scooby/x">a</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/x">b</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/texaschainsaw/x">c</a>
                """ },
            },
            new() // non-game: no S3 links at all
            {
                Id = 3, Slug = "about-us",
                Link = "https://www.spookypinball.com/about-us/",
                Title = new() { Rendered = "About Us" },
                Content = new() { Rendered = "<p>About Spooky.</p>" },
            },
        };

        var filtered = SpookyWpPagesClient.FilterGamePages(pages, S3Host);

        Assert.Single(filtered);
        Assert.Equal(1, filtered[0].Id);
        Assert.Equal("Beetlejuice", filtered[0].Title.Rendered);
    }

    [Fact]
    public void FilterGamePages_TwoSlugPage_IsIncluded()
    {
        // Halloween+Ultraman share the Pinotaur hardware platform — their firmware
        // is hosted under two distinct S3 slugs on the same WP page.  The widened
        // predicate (>= 1 and <= 2) must include this page so both games get slugs
        // written into the machine index and Halloween docs can be linked.
        var twoSlugPage = new SpookyPageRaw
        {
            Id = 1450,
            Slug = "halloween-ultraman",
            Link = "https://www.spookypinball.com/halloween-ultraman/",
            Title = new() { Rendered = "Halloween / Ultraman" },
            Content = new()
            {
                Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.17/code_H78.pkg">Halloween v1.17</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/ultraman/software_versions/v1.00/ultraman.pkg">Ultraman v1.00</a>
                """,
            },
        };

        var filtered = SpookyWpPagesClient.FilterGamePages([twoSlugPage], S3Host);

        Assert.Single(filtered);
        Assert.Equal(1450, filtered[0].Id);
    }

    [Fact]
    public void FilterGamePages_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => SpookyWpPagesClient.FilterGamePages(null!, S3Host));
        Assert.ThrowsAny<ArgumentException>(() => SpookyWpPagesClient.FilterGamePages([], "  "));
    }
}
