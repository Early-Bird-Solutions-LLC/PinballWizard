using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Ap;

/// <summary>
/// Tests for <see cref="ApSitemapClient"/>: the legacy flat urlset
/// filter, and Yoast sitemap-index discovery that keeps WordPress
/// game-page posts while dropping manuals and support URLs.
/// </summary>
public sealed class ApSitemapClientTests
{
    private const string Prefix = "/games/";

    [Fact]
    public void ParseGameUrls_ReturnsOnlyGamePagesUnderPrefix()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/games/oktoberfest</loc></url>
              <url><loc>https://www.american-pinball.com/games/legends-of-valhalla</loc></url>
              <url><loc>https://www.american-pinball.com/about</loc></url>
              <url><loc>https://www.american-pinball.com/news/some-article</loc></url>
              <url><loc>https://www.american-pinball.com/games/</loc></url>
              <url><loc>https://www.american-pinball.com/</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, Prefix);

        Assert.Equal(3, urls.Count);
        Assert.Contains(urls, u => u.AbsolutePath == "/games/houdini");
        Assert.Contains(urls, u => u.AbsolutePath == "/games/oktoberfest");
        Assert.Contains(urls, u => u.AbsolutePath == "/games/legends-of-valhalla");
    }

    [Fact]
    public void ParseGameUrls_RejectsGameSubPages()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/games/houdini/updates</loc></url>
              <url><loc>https://www.american-pinball.com/games/houdini/firmware/v3</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, Prefix);

        Assert.Single(urls);
        Assert.Equal("/games/houdini", urls[0].AbsolutePath);
    }

    [Fact]
    public void ParseGameUrls_HandlesTrailingSlashOnGameUrl()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini/</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, Prefix);

        Assert.Single(urls);
        Assert.EndsWith("/games/houdini/", urls[0].AbsolutePath);
    }

    [Fact]
    public void ParseGameUrls_PrefixWithoutTrailingSlash_StillWorks()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, "/games");
        Assert.Single(urls);
    }

    [Fact]
    public void ParseGameUrls_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ApSitemapClient.ParseGameUrls(null!, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ParseGameUrls_BlankPrefix_Throws(string? prefix)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ApSitemapClient.ParseGameUrls("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"></urlset>", prefix!));
    }

    [Fact]
    public void ParseGamePageCategoryId_ErrorObject_Throws()
    {
        var ex = Assert.Throws<System.Text.Json.JsonException>(() =>
            ApSitemapClient.ParseGamePageCategoryId("{\"code\":\"rest_no_route\"}", "game-page"));
        Assert.Contains("JSON array", ex.Message);
    }

    [Fact]
    public async Task DiscoverGameUrls_FlatUrlset_ReturnsLegacyGamePagesWithoutCategoryLookup()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/about</loc></url>
            </urlset>
            """;

        var (client, gate, handler) = BuildClient(h => h.MapXml($"{BaseUrl}/sitemap.xml", sitemapXml));

        var urls = await client.DiscoverGameUrlsAsync(CancellationToken.None);

        Assert.Single(urls);
        Assert.Equal("/games/houdini", urls[0].AbsolutePath);
        Assert.Equal(handler.Requests.Select(u => u.AbsoluteUri), gate.Acquired.Select(u => u.AbsoluteUri));
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath.Contains("wp-json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverGameUrls_SitemapIndex_KeepsGamePagePostsAndDropsManuals()
    {
        // Captured shape: Yoast sitemap index, then a post urlset that mixes
        // root game permalinks with manuals and news, plus a page urlset that
        // only has the /games/ listing and a nested support page. The
        // game-page category is what distinguishes a machine from a manual.
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?><?xml-stylesheet type="text/xsl" href="//americanpinball.com/wp-content/plugins/wordpress-seo/css/main-sitemap.xsl"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://americanpinball.com/post-sitemap.xml</loc></sitemap>
              <sitemap><loc>https://americanpinball.com/page-sitemap.xml</loc></sitemap>
              <sitemap><loc>https://evil.example/secret-sitemap.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string postSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:image="http://www.google.com/schemas/sitemap-image/1.1" xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://americanpinball.com/houdini/</loc></url>
              <url><loc>https://americanpinball.com/houdini-manual/</loc></url>
              <url><loc>https://americanpinball.com/oktoberfest/</loc></url>
              <url><loc>https://americanpinball.com/cirqus-voltaire-announcement/</loc></url>
            </urlset>
            """;
        const string pageSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://americanpinball.com/games/</loc></url>
              <url><loc>https://americanpinball.com/support/houdini/</loc></url>
              <url><loc>https://www.american-pinball.com/games/legends-of-valhalla</loc></url>
            </urlset>
            """;
        const string categoriesJson = """[{"id":114,"slug":"game-page"}]""";
        const string postsJson = """
            [
              {"slug":"houdini","link":"https://americanpinball.com/houdini/"},
              {"slug":"oktoberfest","link":"https://americanpinball.com/oktoberfest/"},
              {"slug":"galactic-tank-force","link":"https://americanpinball.com/galactic-tank-force/"},
              {"slug":"stolen","link":"https://evil.example/stolen/"}
            ]
            """;

        var (client, gate, handler) = BuildClient(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", indexXml)
            .MapXml("https://americanpinball.com/post-sitemap.xml", postSitemapXml)
            .MapXml("https://americanpinball.com/page-sitemap.xml", pageSitemapXml)
            .MapJson($"{BaseUrl}/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug", categoriesJson)
            .MapJson($"{BaseUrl}/wp-json/wp/v2/posts?categories=114&per_page=100&page=1&_fields=slug,link", postsJson));

        var urls = await client.DiscoverGameUrlsAsync(CancellationToken.None);

        Assert.Equal(
            [
                "https://www.american-pinball.com/games/legends-of-valhalla",
                "https://americanpinball.com/houdini/",
                "https://americanpinball.com/oktoberfest/",
                "https://americanpinball.com/galactic-tank-force/",
            ],
            urls.Select(u => u.AbsoluteUri).ToArray());
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Contains("manual", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Contains("announcement", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.StartsWith("/support/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Equals("/games/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(urls, u => u.Host.Equals("evil.example", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, u => u.Host.Equals("evil.example", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Select(u => u.AbsoluteUri), gate.Acquired.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task DiscoverGameUrls_IndexWithNoGamePages_Throws()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://americanpinball.com/post-sitemap.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string postSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://americanpinball.com/houdini-manual/</loc></url>
            </urlset>
            """;

        var (client, _, _) = BuildClient(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", indexXml)
            .MapXml("https://americanpinball.com/post-sitemap.xml", postSitemapXml)
            .MapJson($"{BaseUrl}/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug", """[{"id":114,"slug":"game-page"}]""")
            .MapJson($"{BaseUrl}/wp-json/wp/v2/posts?categories=114&per_page=100&page=1&_fields=slug,link", "[]"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverGameUrlsAsync(CancellationToken.None));
        Assert.Contains("0 game-page URLs", ex.Message);
    }

    [Fact]
    public async Task DiscoverGameUrls_CapturedYoastIndex_ReturnsLiveGamePermalinksOnly()
    {
        var indexXml = File.ReadAllText(FixtureFile("sitemap-index.captured.xml"));
        var postSitemap = File.ReadAllText(FixtureFile("post-sitemap.captured.xml"));
        var postsJson = File.ReadAllText(FixtureFile("game-page-posts.captured.json"));
        var children = ApSitemapClient.ParseChildSitemapUrls(indexXml);
        Assert.NotEmpty(children);

        var (client, gate, handler) = BuildClient(mapped =>
        {
            mapped.MapXml($"{BaseUrl}/sitemap.xml", indexXml);
            mapped.MapJson(
                $"{BaseUrl}/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug",
                File.ReadAllText(FixtureFile("game-page-category.captured.json")));
            mapped.MapJson(
                $"{BaseUrl}/wp-json/wp/v2/posts?categories=114&per_page=100&page=1&_fields=slug,link",
                postsJson);
            foreach (var child in children)
            {
                var fileName = Path.GetFileNameWithoutExtension(child.AbsolutePath) + ".captured.xml";
                mapped.MapXml(child.AbsoluteUri, File.ReadAllText(FixtureFile(fileName)));
            }
        });

        var urls = await client.DiscoverGameUrlsAsync(CancellationToken.None);

        // Literals, not ParseGamePagePosts: a parser bug must not satisfy both sides.
        string[] expected =
        [
            "https://americanpinball.com/barry-os-bbq-challenge/",
            "https://americanpinball.com/cirqus-voltaire/",
            "https://americanpinball.com/galactic-tank-force/",
            "https://americanpinball.com/hot-wheels/",
            "https://americanpinball.com/houdini/",
            "https://americanpinball.com/legends-of-valhalla/",
            "https://americanpinball.com/oktoberfest/",
        ];
        Assert.Contains("https://americanpinball.com/houdini-manual/", postSitemap, StringComparison.Ordinal);
        Assert.Equal(expected, urls.Select(uri => uri.AbsoluteUri).OrderBy(uri => uri, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(urls, uri => uri.AbsoluteUri.Contains("manual", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1 + children.Count + 2, handler.Requests.Count);
        Assert.Equal(handler.Requests.Select(uri => uri.AbsoluteUri), gate.Acquired.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task DiscoverGameUrls_EmptySitemapIndex_ThrowsBeforeChildFetches()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"></sitemapindex>
            """;

        var (client, _, handler) = BuildClient(h => h.MapXml($"{BaseUrl}/sitemap.xml", indexXml));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverGameUrlsAsync(CancellationToken.None));
        Assert.Contains("no child sitemaps", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DiscoverGameUrls_FullPageWithTotalPagesHeader_DoesNotRequestTheNextPage()
    {
        // A full page used to imply "fetch page+1". WordPress answers that
        // with 400 rest_post_invalid_page_number when X-WP-TotalPages says
        // this page is the last one, and the games already read were dropped.
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://americanpinball.com/post-sitemap.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string postSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://americanpinball.com/houdini/</loc></url>
              <url><loc>https://americanpinball.com/oktoberfest/</loc></url>
            </urlset>
            """;
        const string postsJson = """
            [
              {"slug":"houdini","link":"https://americanpinball.com/houdini/"},
              {"slug":"oktoberfest","link":"https://americanpinball.com/oktoberfest/"}
            ]
            """;

        var (client, _, handler) = BuildClient(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", indexXml)
            .MapXml("https://americanpinball.com/post-sitemap.xml", postSitemapXml)
            .MapJson($"{BaseUrl}/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug", """[{"id":114,"slug":"game-page"}]""")
            .Map(PostsUrl(page: 1, pageSize: 2), _ => JsonPage(postsJson, totalPages: "1")),
            new ApOptions { BaseUrl = BaseUrl, CategoryPageSize = 2 });

        var urls = await client.DiscoverGameUrlsAsync(CancellationToken.None);

        Assert.Equal(2, urls.Count);
        Assert.DoesNotContain(handler.Requests, u => u.Query.Contains("&page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverGameUrls_TotalPagesHeader_ReadsTheFollowingPage()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://americanpinball.com/post-sitemap.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string postSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"></urlset>
            """;

        var (client, _, handler) = BuildClient(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", indexXml)
            .MapXml("https://americanpinball.com/post-sitemap.xml", postSitemapXml)
            .MapJson($"{BaseUrl}/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug", """[{"id":114,"slug":"game-page"}]""")
            .Map(PostsUrl(page: 1, pageSize: 2), _ => JsonPage(
                """[{"slug":"houdini","link":"https://americanpinball.com/houdini/"},{"slug":"oktoberfest","link":"https://americanpinball.com/oktoberfest/"}]""",
                totalPages: "2"))
            .Map(PostsUrl(page: 2, pageSize: 2), _ => JsonPage(
                """[{"slug":"hot-wheels","link":"https://americanpinball.com/hot-wheels/"}]""",
                totalPages: "2")),
            new ApOptions { BaseUrl = BaseUrl, CategoryPageSize = 2 });

        var urls = await client.DiscoverGameUrlsAsync(CancellationToken.None);

        Assert.Equal(
            [
                "https://americanpinball.com/houdini/",
                "https://americanpinball.com/oktoberfest/",
                "https://americanpinball.com/hot-wheels/",
            ],
            urls.Select(u => u.AbsoluteUri).ToArray());
        Assert.Contains(handler.Requests, u => u.Query.Contains("&page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverGameUrls_MissingCategory_ThrowsEvenWhenLegacyGameUrlExists()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://americanpinball.com/page-sitemap.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string pageSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
            </urlset>
            """;

        var (client, _, _) = BuildClient(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", indexXml)
            .MapXml("https://americanpinball.com/page-sitemap.xml", pageSitemapXml)
            .MapJson($"{BaseUrl}/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug", "[]"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverGameUrlsAsync(CancellationToken.None));
        Assert.Contains("game-page", ex.Message);
    }

    [Fact]
    public void FixtureFile_RootedName_KeepsTheFixtureDirectory()
    {
        // Path.Combine(base, "/sitemap-index.captured.xml") returns only the
        // rooted argument and drops base. This must still land on the captured file.
        var rooted = Path.DirectorySeparatorChar + "sitemap-index.captured.xml";
        var path = FixtureFile(rooted);
        var expected = Path.Combine(Path.GetFullPath(FixtureDir()), "sitemap-index.captured.xml");

        Assert.Equal(expected, path);
        Assert.True(File.Exists(path));
        Assert.NotEqual(Path.GetFullPath(rooted), path);
    }

    private const string BaseUrl = "https://www.american-pinball.com";

    private static string FixtureFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // A rooted later argument makes Path.Combine discard every earlier segment.
        var segment = Path.IsPathRooted(fileName) ? Path.GetFileName(fileName) : fileName;
        if (string.IsNullOrEmpty(segment)
            || segment.Contains("..", StringComparison.Ordinal)
            || segment.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || segment.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixture file name must be a single relative segment: {fileName}");
        }

        var root = Path.GetFullPath(FixtureDir());
        var combined = Path.GetFullPath(Path.Combine(root, segment));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixture path escaped {root}: {combined}");
        }

        return combined;
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root from the test assembly.");
        }

        return Path.Combine(dir.FullName, "tests", "PinballWizard.Infrastructure.Tests", "Fixtures", "Ap");
    }

    private static string PostsUrl(int page, int pageSize) =>
        $"{BaseUrl}/wp-json/wp/v2/posts?categories=114&per_page={pageSize}&page={page}&_fields=slug,link";

    private static HttpResponseMessage JsonPage(string json, string totalPages)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("X-WP-TotalPages", totalPages);
        return response;
    }

    private static (ApSitemapClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler, ApOptions? options = null)
    {
        options ??= new ApOptions { BaseUrl = BaseUrl };
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);
        var client = new ApSitemapClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate,
            Options.Create(new PolitenessOptions()),
            Options.Create(options),
            NullLogger<ApSitemapClient>.Instance);
        return (client, gate, handler);
    }
}
