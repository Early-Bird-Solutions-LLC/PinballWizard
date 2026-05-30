using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Direct tests for the robots.txt parser. The cache + fetch logic is
/// tested separately in <see cref="RobotsTxtCacheTests"/>; these
/// exercise the parser against literal robots.txt strings.
/// </summary>
public sealed class RobotsTxtParserTests
{
    [Fact]
    public void Parse_EmptyFile_AllowsEverything()
    {
        var rules = RobotsTxtParser.Parse(string.Empty);
        Assert.True(rules.IsAllowed("/anything", "Anyone"));
    }

    [Fact]
    public void Parse_WildcardDisallow_BlocksMatchingPath()
    {
        const string body = """
            User-agent: *
            Disallow: /private/
            """;

        var rules = RobotsTxtParser.Parse(body);

        Assert.False(rules.IsAllowed("/private/secret.txt", "PinballWizard/0.1"));
        Assert.True(rules.IsAllowed("/public/", "PinballWizard/0.1"));
    }

    [Fact]
    public void Parse_AgentSpecificBlockTakesPrecedence()
    {
        const string body = """
            User-agent: *
            Disallow: /

            User-agent: PinballWizard
            Allow: /public/
            Disallow: /admin/
            """;

        var rules = RobotsTxtParser.Parse(body);

        Assert.True(rules.IsAllowed("/public/index.html", "PinballWizard/0.1"));
        Assert.False(rules.IsAllowed("/admin/", "PinballWizard/0.1"));
        // A different agent gets the wildcard block.
        Assert.False(rules.IsAllowed("/public/index.html", "OtherBot"));
    }

    [Fact]
    public void Parse_LongestMatchWins()
    {
        const string body = """
            User-agent: *
            Disallow: /a/
            Allow: /a/public/
            """;

        var rules = RobotsTxtParser.Parse(body);

        // Disallow /a/ wins over nothing for /a/private
        Assert.False(rules.IsAllowed("/a/private", "agent"));
        // Allow /a/public/ wins over Disallow /a/ because it is more specific (longer)
        Assert.True(rules.IsAllowed("/a/public/page.html", "agent"));
    }

    [Fact]
    public void Parse_EmptyDisallowMeansAllowAll()
    {
        const string body = """
            User-agent: *
            Disallow:
            """;

        var rules = RobotsTxtParser.Parse(body);

        Assert.True(rules.IsAllowed("/anything", "agent"));
    }

    [Fact]
    public void Parse_WildcardAndDollarPatterns()
    {
        const string body = """
            User-agent: *
            Disallow: /*.json$
            """;

        var rules = RobotsTxtParser.Parse(body);

        Assert.False(rules.IsAllowed("/data.json", "agent"));
        Assert.True(rules.IsAllowed("/data.json/sub", "agent"));
        Assert.True(rules.IsAllowed("/data.html", "agent"));
    }

    [Fact]
    public void Parse_RecordsCrawlDelayAndSitemaps()
    {
        const string body = """
            Sitemap: https://example.com/sitemap.xml
            Sitemap: https://example.com/sitemap-news.xml

            User-agent: *
            Crawl-delay: 5
            Disallow: /private/
            """;

        var rules = RobotsTxtParser.Parse(body);

        Assert.Equal(2, rules.Sitemaps.Count);
        Assert.Contains("https://example.com/sitemap.xml", rules.Sitemaps);
    }

    [Fact]
    public void Parse_IgnoresComments()
    {
        const string body = """
            # This is a comment
            User-agent: *  # inline comment
            Disallow: /private/  # another comment
            """;

        var rules = RobotsTxtParser.Parse(body);

        Assert.False(rules.IsAllowed("/private/secret", "agent"));
        Assert.True(rules.IsAllowed("/public/", "agent"));
    }
}
