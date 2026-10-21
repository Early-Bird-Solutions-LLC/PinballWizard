using AngleSharp.Html.Parser;
using PinballWizard.Infrastructure.Scraping.Ap;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Ap;

public sealed class ApSupportPageParserTests
{
    private static readonly string[] ExpectedGameSupportPages =
    [
        "https://americanpinball.com/support/barry-os-bbq-challenge/",
        "https://americanpinball.com/support/galactic-tank-force/",
        "https://americanpinball.com/support/hot-wheels/",
        "https://americanpinball.com/support/houdini/",
        "https://americanpinball.com/support/legends-of-valhalla/",
        "https://americanpinball.com/support/oktoberfest/",
    ];

    [Fact]
    public void ParseTopLevelPageId_CapturedLookup_ReturnsSupportPageId()
    {
        var id = ApSupportPageParser.ParseTopLevelPageId(ApFixtures.Read("support-page-lookup.captured.json"), "support");

        Assert.Equal(2631, id);
    }

    [Fact]
    public void ParseTopLevelPageId_SameSlugUnderAnotherParent_IsNotTheSupportPage()
    {
        const string json = """[{"id":77,"slug":"support","link":"https://americanpinball.com/about/support/","parent":12}]""";

        Assert.Null(ApSupportPageParser.ParseTopLevelPageId(json, "support"));
    }

    [Fact]
    public void ParseTopLevelPageId_NonArrayBody_Throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => ApSupportPageParser.ParseTopLevelPageId("""{"code":"rest_no_route"}""", "support"));
    }

    [Fact]
    public void SelectGameSupportPages_CapturedChildrenAndGameCatalog_KeepsOnlyPerGameHubs()
    {
        var children = ApSupportPageParser.ParsePages(ApFixtures.Read("support-child-pages.captured.json"));
        var gameSlugs = GameSlugsFromCapturedCatalog();
        var notGames = new List<string>();

        var selected = ApSupportPageParser.SelectGameSupportPages(children, gameSlugs, notGames);

        Assert.Equal(8, children.Count);
        Assert.Equal(
            ExpectedGameSupportPages,
            selected.Select(p => p.Link.AbsoluteUri).OrderBy(u => u, StringComparer.Ordinal).ToArray());
        Assert.Equal(["register", "updates"], notGames.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        // cirqus-voltaire is a game with no support hub yet; nothing invents one.
        Assert.Contains("cirqus-voltaire", gameSlugs);
        Assert.DoesNotContain(selected, p => p.Slug == "cirqus-voltaire");
    }

    [Fact]
    public void SelectGameSupportPages_CapturedData_MatchesTheHubsTheRenderedIndexLinks()
    {
        // Oracle check: WordPress-metadata discovery must find exactly the
        // per-game hubs a visitor sees linked from the captured /support/ index.
        var indexUrl = new Uri("https://americanpinball.com/support/");
        using var index = new HtmlParser().ParseDocument(ApFixtures.Read("support-index.captured.html"));
        var linkedHubs = index.QuerySelectorAll("a[href]")
            .Select(a => new Uri(indexUrl, a.GetAttribute("href")!))
            .Where(u => u.Host == indexUrl.Host)
            .Select(u => u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segments => segments.Length == 2 && segments[0] == "support")
            .Select(segments => $"https://americanpinball.com/support/{segments[1]}/")
            .Where(u => !u.EndsWith("/register/", StringComparison.Ordinal) && !u.EndsWith("/updates/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToArray();

        var selected = ApSupportPageParser.SelectGameSupportPages(
            ApSupportPageParser.ParsePages(ApFixtures.Read("support-child-pages.captured.json")),
            GameSlugsFromCapturedCatalog());

        Assert.Equal(ExpectedGameSupportPages, linkedHubs);
        Assert.Equal(linkedHubs, selected.Select(p => p.Link.AbsoluteUri).OrderBy(u => u, StringComparer.Ordinal).ToArray());
    }

    private static HashSet<string> GameSlugsFromCapturedCatalog() =>
        ApSitemapClient.ParseGamePagePosts(ApFixtures.Read("game-page-posts.captured.json"))
            .Select(p => p.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
