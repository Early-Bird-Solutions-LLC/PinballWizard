using PinballWizard.Infrastructure.Scraping.Spooky;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.Spooky;

/// <summary>
/// Tests for <see cref="SpookyGamePageExtractor"/>. Spooky game pages
/// come from the WP REST API as JSON; the extractor's job is to:
/// (1) confirm the page is really a single-game page (single S3 slug
/// in content), (2) prefer the S3 slug as the canonical game slug
/// even when the WP page slug is numeric (<c>2486-2</c>), (3) decode
/// HTML entities in the title, (4) collect every S3-hosted firmware
/// URL, deduped, with anchor-text labels where present.
/// </summary>
public sealed class SpookyGamePageExtractorTests
{
    private const string S3Host = "spookypinball.s3.us-east-2.amazonaws.com";

    [Fact]
    public void ExtractGame_SingleSlug_BuildsExpectedRecord()
    {
        var page = new SpookyPageRaw
        {
            Id = 6438,
            Slug = "beetlejuice",
            Link = "https://www.spookypinball.com/beetlejuice/",
            Title = new() { Rendered = "Beetlejuice" },
            Content = new()
            {
                Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/software_versions/release/v1.beetlejuice">v1</a>
                """,
            },
        };

        var record = SpookyGamePageExtractor.ExtractGame(page, S3Host);

        Assert.NotNull(record);
        Assert.Equal("Beetlejuice", record!.Title);
        Assert.Equal("beetlejuice", record.Slug);
        Assert.Equal("game_spooky_beetlejuice", record.GameId);
        Assert.Equal("https://www.spookypinball.com/beetlejuice/", record.GamePageUrl);
        Assert.Equal(["spooky_wp_pages"], record.DiscoveredOn);
        Assert.NotNull(record.Source);
        Assert.Equal("https://www.spookypinball.com/beetlejuice/", record.Source!.ScrapedFrom);
    }

    [Fact]
    public void ExtractGame_NumericWpSlug_UsesS3SlugAsCanonical()
    {
        // Real-world case: TCM's WP slug is "2486-2" but the S3 URLs use "texaschainsaw".
        var page = new SpookyPageRaw
        {
            Id = 2486,
            Slug = "2486-2",
            Link = "https://www.spookypinball.com/2486-2/",
            Title = new() { Rendered = "Texas Chainsaw Massacre" },
            Content = new()
            {
                Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/texaschainsaw/software_versions/v1.00/tcm-1_00.pkg">v1.00</a>
                """,
            },
        };

        var record = SpookyGamePageExtractor.ExtractGame(page, S3Host);

        Assert.NotNull(record);
        Assert.Equal("texaschainsaw", record!.Slug);
        Assert.Equal("game_spooky_texaschainsaw", record.GameId);
    }

    [Fact]
    public void ExtractGame_HtmlEntitiesInTitle_AreDecoded()
    {
        var page = new SpookyPageRaw
        {
            Id = 1,
            Slug = "alice",
            Link = "https://www.spookypinball.com/alice/",
            Title = new() { Rendered = "Alice Cooper&#8217;s Nightmare Castle" },
            Content = new()
            {
                Rendered = """<a href="https://spookypinball.s3.us-east-2.amazonaws.com/alice/x">x</a>""",
            },
        };

        var record = SpookyGamePageExtractor.ExtractGame(page, S3Host);

        Assert.NotNull(record);
        Assert.Equal("Alice Cooper’s Nightmare Castle", record!.Title);
    }

    [Fact]
    public void ExtractGame_AggregatorPage_ReturnsNull()
    {
        var page = new SpookyPageRaw
        {
            Id = 2445,
            Slug = "2445-2",
            Link = "https://www.spookypinball.com/2445-2/",
            Title = new() { Rendered = "SCOOBY BASE IMAGE UPDATE" },
            Content = new()
            {
                Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/scooby/x">a</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/x">b</a>
                """,
            },
        };

        var record = SpookyGamePageExtractor.ExtractGame(page, S3Host);
        Assert.Null(record);
    }

    [Fact]
    public void ExtractGame_NoS3Links_ReturnsNull()
    {
        var page = new SpookyPageRaw
        {
            Id = 477,
            Slug = "about-us",
            Link = "https://www.spookypinball.com/about-us/",
            Title = new() { Rendered = "About Us" },
            Content = new() { Rendered = "<p>About Spooky.</p>" },
        };

        var record = SpookyGamePageExtractor.ExtractGame(page, S3Host);
        Assert.Null(record);
    }

    [Fact]
    public void ExtractGame_EmptyTitle_ReturnsNull()
    {
        var page = new SpookyPageRaw
        {
            Id = 1,
            Slug = "x",
            Link = "https://www.spookypinball.com/x/",
            Title = new() { Rendered = "  " },
            Content = new()
            {
                Rendered = """<a href="https://spookypinball.s3.us-east-2.amazonaws.com/x/y">y</a>""",
            },
        };

        Assert.Null(SpookyGamePageExtractor.ExtractGame(page, S3Host));
    }

    [Fact]
    public void ExtractGame_EmptyLink_ReturnsNull()
    {
        var page = new SpookyPageRaw
        {
            Id = 1,
            Slug = "x",
            Link = "",
            Title = new() { Rendered = "X" },
            Content = new()
            {
                Rendered = """<a href="https://spookypinball.s3.us-east-2.amazonaws.com/x/y">y</a>""",
            },
        };

        Assert.Null(SpookyGamePageExtractor.ExtractGame(page, S3Host));
    }

    [Fact]
    public void ExtractDownloads_ReturnsAllS3UrlsDedupedWithAnchorText()
    {
        var page = new SpookyPageRaw
        {
            Id = 1450,
            Slug = "halloween",
            Link = "https://www.spookypinball.com/game-support/halloween/",
            Title = new() { Rendered = "Halloween" },
            Content = new()
            {
                Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.17/code_H78.pkg">v1.17</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.18/code_H78.pkg">v1.18</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.18/code_H78.pkg">duplicate</a>
                    <a href="https://example.com/external">External</a>
                """,
            },
        };

        var links = SpookyGamePageExtractor.ExtractDownloads(page, S3Host);

        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal("halloween", l.GameSlug));
        Assert.All(links, l => Assert.Equal("Spooky Pinball Game Page", l.DiscoveryContext));
        Assert.Contains(links, l => l.LinkText == "v1.17");
        Assert.Contains(links, l => l.LinkText == "v1.18");
    }

    [Fact]
    public void ExtractDownloads_AggregatorPage_ReturnsEmpty()
    {
        var page = new SpookyPageRaw
        {
            Id = 2445,
            Slug = "2445-2",
            Link = "https://www.spookypinball.com/2445-2/",
            Title = new() { Rendered = "SCOOBY BASE IMAGE UPDATE" },
            Content = new()
            {
                Rendered = """
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/scooby/x">a</a>
                    <a href="https://spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/x">b</a>
                """,
            },
        };

        var links = SpookyGamePageExtractor.ExtractDownloads(page, S3Host);
        Assert.Empty(links);
    }

    [Fact]
    public void ExtractDownloads_NoS3Links_ReturnsEmpty()
    {
        var page = new SpookyPageRaw
        {
            Id = 1,
            Slug = "x",
            Link = "https://www.spookypinball.com/x/",
            Title = new() { Rendered = "X" },
            Content = new() { Rendered = "<p>nothing here</p>" },
        };

        var links = SpookyGamePageExtractor.ExtractDownloads(page, S3Host);
        Assert.Empty(links);
    }

    [Fact]
    public void ExtractDownloads_EmptyContent_ReturnsEmpty()
    {
        var page = new SpookyPageRaw
        {
            Id = 1,
            Slug = "x",
            Link = "https://www.spookypinball.com/x/",
            Title = new() { Rendered = "X" },
            Content = new() { Rendered = "" },
        };

        var links = SpookyGamePageExtractor.ExtractDownloads(page, S3Host);
        Assert.Empty(links);
    }

    [Fact]
    public void ExtractGame_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => SpookyGamePageExtractor.ExtractGame(null!, S3Host));
        Assert.ThrowsAny<ArgumentException>(() =>
            SpookyGamePageExtractor.ExtractGame(new SpookyPageRaw(), "  "));
    }

    [Fact]
    public void ExtractDownloads_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => SpookyGamePageExtractor.ExtractDownloads(null!, S3Host));
        Assert.ThrowsAny<ArgumentException>(() =>
            SpookyGamePageExtractor.ExtractDownloads(new SpookyPageRaw(), "  "));
    }
}
