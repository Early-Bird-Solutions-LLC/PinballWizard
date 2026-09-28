using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Kineticist;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Kineticist;

/// <summary>
/// Unit tests for <see cref="KineticistTutorialsClient"/>.
/// Fixtures are inline (no external files) — matches the scraper-test
/// convention (see ApBulletinExtractorTests, SpookyGamePageExtractorTests).
/// </summary>
public sealed class KineticistTutorialsClientTests
{
    private const string BaseUrl = "https://www.kineticist.com";
    private const string SitemapUrl = BaseUrl + "/sitemap/news.xml";

    // ── Inline fixtures ─────────────────────────────────────────────────────────

    // Shape of https://www.kineticist.com/sitemap/news.xml (probed 2026-09-28):
    // a sitemaps.org urlset of every /news/{slug}. Tutorials are the slugs with a
    // "tutorial" token; the rest (interviews, "-pinball" news, "-guide" features,
    // the category page itself, off-host entries) must be excluded.
    private const string NewsSitemapXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
        <url><loc>https://www.kineticist.com/news/moving-units-8</loc><lastmod>2026-09-28T15:21:09.339Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/transformers-pinball-tutorial</loc><lastmod>2026-06-25T12:00:00.000Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/dolly-parton-pinball</loc><lastmod>2026-09-10T12:41:23.983Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/monster-bash-pinball-tutorial</loc><lastmod>2025-10-29T12:00:00.000Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/a-beginners-guide-to-pinball-designers</loc><lastmod>2026-01-01T00:00:00.000Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/simpsons-pinball-party-tutorial-advanced</loc><lastmod>2026-05-01T00:00:00.000Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/category/pinball-tutorial</loc><lastmod>2026-09-23T20:19:29.847Z</lastmod></url>
        <url><loc>https://www.kineticist.com/news/author/noah-crable</loc></url>
        <url><loc>https://www.kineticist.com/news/Transformers-Pinball-Tutorial/</loc></url>
        <url><loc>https://twip.kineticist.com/news/other-host-tutorial</loc></url>
        </urlset>
        """;

    // Real .md body for Transformers (representative of the actual Kineticist format
    // probed 2026-06-25): H1 title, "by [Author](/author/...) · Date · [Category]",
    // blockquote, body, and the closing "## Related - Game:" block. Deliberately
    // includes the real-article trap: an inline narrative link to a DIFFERENT game
    // (John Wick) that appears BEFORE the subject — the canonical slug must come
    // from the structured "- Game:" line, not the first body link.
    private const string TransformersMdBody = """
        # Autobots, Transform and Roll Out!

        by [Noah Crable](/author/noah-crable) · June 25, 2026 · [Pinball Tutorial](/news/category/pinball-tutorial)

        > Learn to play Stern Pinball's 2026 release, Transformers: More Than Meets the Eye in our latest tutorial.

        ## About Transformers: More Than Meets the Eye

        After being mentored by Gomez on [John Wick](https://www.kineticist.com/games/pinball/john-wick-2024) in 2024, the designer brought that pace to [Transformers: More Than Meets the Eye](/games/pinball/transformers-more-than-meets-the-eye).

        - **Manufacturer:** Stern
        - **Release Year:** 2026

        ## Getting Started

        Shoot the Megatron scoop to start a mission. Two missions lights One Shall Fall.

        ### Skill Shot

        Plunge the ball softly to the upper flipper and hit any lit shot. Max value: 8M.

        ## Strategies

        Focus on Autobot Run or Prime Target first, then qualify Transformers Multiball.

        ## Related
        - Game: [Transformers: More Than Meets the Eye](/games/pinball/transformers-more-than-meets-the-eye)

        ---
        https://www.kineticist.com/news/transformers-pinball-tutorial
        """;

    private const string MonsterBashMdBody = """
        # Rock Monster: Learn to Play Williams Monster Bash Pinball

        by [James McFatter](/author/james-mcfatter) · October 29, 2025 · [Pinball Tutorial](/news/category/pinball-tutorial)

        > Learn how to play the 1998 Williams release, Monster Bash pinball.

        ## About Monster Bash

        Monster Bash is a multi-ball heavy game featuring six classic movie monsters.

        - **Manufacturer:** Williams
        - **Release Year:** 1998

        ## Getting Started

        Complete monster bands to light Monster Bash multiball.

        https://www.kineticist.com/news/monster-bash-pinball-tutorial
        """;

    // ── DeriveGameSlug ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("transformers-pinball-tutorial", "transformers")]
    [InlineData("monster-bash-pinball-tutorial", "monster-bash")]
    [InlineData("godzilla-pinball-tutorial", "godzilla")]
    [InlineData("the-walking-dead-pinball-tutorial", "the-walking-dead")]
    [InlineData("dungeons-and-dragons-tutorial", "dungeons-and-dragons")]
    [InlineData("how-to-play-dracula-pinball-tutorial", "how-to-play-dracula")]
    [InlineData("eight-ball-deluxe-rules", "eight-ball-deluxe")]
    [InlineData("foo-fighters-pinball-tutorial", "foo-fighters")]
    [InlineData("godzilla-strategy", "godzilla")]
    public void DeriveGameSlug_KnownSuffixes_StripsCorrectly(string articleSlug, string expectedGameSlug)
    {
        var result = KineticistTutorialsClient.DeriveGameSlug(articleSlug);
        Assert.Equal(expectedGameSlug, result);
    }

    [Fact]
    public void DeriveGameSlug_NoKnownSuffix_ReturnsFallback()
    {
        // Edge case: slug doesn't match any known suffix pattern — returned as-is.
        var result = KineticistTutorialsClient.DeriveGameSlug("some-slug-without-suffix");
        Assert.Equal("some-slug-without-suffix", result);
    }

    // ── ExtractCanonicalGameSlug ────────────────────────────────────────────────

    [Fact]
    public void ExtractCanonicalGameSlug_RelatedGameLink_ReturnsExactSlug()
    {
        // The structured "- Game:" line carries the exact Kineticist slug, which
        // differs from both the article stem and any inline mention.
        const string md = """
            # Choose Your House: How to Play Stern's Game of Thrones Pinball

            Body text mentioning other games.

            ## Related
            - Game: [Game of Thrones](/games/pinball/game-of-thrones)

            https://www.kineticist.com/news/got-pinball-tutorial
            """;

        Assert.Equal("game-of-thrones", KineticistTutorialsClient.ExtractCanonicalGameSlug(md));
    }

    [Fact]
    public void ExtractCanonicalGameSlug_AbsoluteUrl_ReturnsSlug()
    {
        // The "- Game:" href may be absolute (https://www.kineticist.com/...) too.
        const string md = "## Related\n- Game: [Pokémon](https://www.kineticist.com/games/pinball/pokemon-2026)";
        Assert.Equal("pokemon-2026", KineticistTutorialsClient.ExtractCanonicalGameSlug(md));
    }

    [Fact]
    public void ExtractCanonicalGameSlug_InlineLinkToDifferentGame_IgnoredInFavorOfRelatedLine()
    {
        // Real Transformers case: an inline body link to John Wick precedes the
        // subject. Only the structured "- Game:" line is read.
        const string md = """
            Mentored on [John Wick](https://www.kineticist.com/games/pinball/john-wick-2024) first.

            ## Related
            - Game: [Transformers](/games/pinball/transformers-more-than-meets-the-eye)
            """;

        Assert.Equal("transformers-more-than-meets-the-eye", KineticistTutorialsClient.ExtractCanonicalGameSlug(md));
    }

    [Fact]
    public void ExtractCanonicalGameSlug_NoRelatedBlock_ReturnsNull()
    {
        const string md = "# A Tutorial\n\nBody with no Related block.\n\nhttps://www.kineticist.com/news/x";
        Assert.Null(KineticistTutorialsClient.ExtractCanonicalGameSlug(md));
    }

    // ── DiscoverTutorialSlugsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_NewsSitemap_ReturnsOnlyTutorialSlugs()
    {
        var (client, gate, handler) = BuildClient(h => h.MapXml(SitemapUrl, NewsSitemapXml));

        var slugs = await client.DiscoverTutorialSlugsAsync(CancellationToken.None);

        string[] expected = ["monster-bash-pinball-tutorial", "simpsons-pinball-party-tutorial-advanced", "transformers-pinball-tutorial"];
        Assert.Equal(expected, slugs.Order(StringComparer.Ordinal));

        // One machine-consumer request replaces paging the rendered category
        // listing — and it went through the gate.
        Assert.Equal(new[] { new Uri(SitemapUrl) }, handler.Requests);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_NotASitemap_ThrowsInsteadOfReturningEmpty()
    {
        // A challenge page or an HTML error served with 200 must not read as
        // "zero tutorials this week".
        var (client, _, _) = BuildClient(h => h.MapHtml(SitemapUrl, "<!DOCTYPE html><html><body>Checkpoint</body></html>"));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.DiscoverTutorialSlugsAsync(CancellationToken.None));

        Assert.Contains(SitemapUrl, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_SitemapIndexInsteadOfUrlset_Throws()
    {
        const string index = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.kineticist.com/sitemap/news.xml</loc></sitemap>
            </sitemapindex>
            """;
        var (client, _, _) = BuildClient(h => h.MapXml(SitemapUrl, index));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.DiscoverTutorialSlugsAsync(CancellationToken.None));

        Assert.Contains("sitemapindex", ex.Message, StringComparison.Ordinal);
    }

    // ── 429 handling through the real politeness gate ───────────────────────────

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_429WithRetryAfterThen200_WaitsTheRetryAfterAndCollectsTutorials()
    {
        var sitemapCalls = 0;
        var (client, time, handler) = BuildClientWithRealGate(new PolitenessOptions { RequestDelayMs = 5_000 }, h => h
            .Map(SitemapUrl, _ => ++sitemapCalls == 1
                ? TooManyRequests(r => r.Headers.TryAddWithoutValidation("Retry-After", "90"))
                : Xml(NewsSitemapXml)));

        var slugs = await client.DiscoverTutorialSlugsAsync(CancellationToken.None);

        Assert.Equal(3, slugs.Count);
        Assert.Contains("transformers-pinball-tutorial", slugs);
        Assert.Equal(2, handler.Requests.Count);
        // The second request waited out the source's Retry-After (90 s), not the
        // 5 s pacing delay and not a Polly-style 2 s retry.
        Assert.Equal(new[] { TimeSpan.FromSeconds(90) }, time.Waits);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_429WithHttpDateRetryAfterThen200_WaitsUntilThatDate()
    {
        var sitemapCalls = 0;
        var (client, time, _) = BuildClientWithRealGate(new PolitenessOptions(), h => h
            .Map(SitemapUrl, _ => ++sitemapCalls == 1
                ? TooManyRequests(r =>
                {
                    r.Headers.TryAddWithoutValidation("Date", "Sun, 27 Sep 2026 11:00:00 GMT");
                    r.Headers.TryAddWithoutValidation("Retry-After", "Sun, 27 Sep 2026 11:03:00 GMT");
                })
                : Xml(NewsSitemapXml)));

        var slugs = await client.DiscoverTutorialSlugsAsync(CancellationToken.None);

        Assert.Equal(3, slugs.Count);
        Assert.Equal(new[] { TimeSpan.FromMinutes(3) }, time.Waits);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_Persistent429_FailsWithClearErrorAfterTheBudget()
    {
        var options = new PolitenessOptions { Max429Streak = 3, RateLimitBackoffMs = 60_000, MaxRetryAfterSeconds = 600 };
        var (client, time, handler) = BuildClientWithRealGate(options, h => h
            .Map(SitemapUrl, _ => TooManyRequests()));

        var ex = await Assert.ThrowsAsync<PolitenessException>(
            () => client.DiscoverTutorialSlugsAsync(CancellationToken.None));

        Assert.Equal(PolitenessViolation.TooMany429Responses, ex.Violation);
        Assert.Equal(new Uri(SitemapUrl), ex.Url);
        Assert.Contains("www.kineticist.com", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 times in a row", ex.Message, StringComparison.Ordinal);
        // Budget: Max429Streak re-sends, each after a doubling backoff — then stop.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240) },
            time.Waits);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_VercelChallenge_FailsOnFirstResponseWithoutRetrying()
    {
        // The live 2026-09-28 response: 429 + x-vercel-mitigated: challenge, no Retry-After.
        var (client, time, handler) = BuildClientWithRealGate(new PolitenessOptions(), h => h
            .Map(SitemapUrl, _ => TooManyRequests(r => r.Headers.TryAddWithoutValidation("x-vercel-mitigated", "challenge"))));

        var ex = await Assert.ThrowsAsync<PolitenessException>(
            () => client.DiscoverTutorialSlugsAsync(CancellationToken.None));

        Assert.Equal(PolitenessViolation.BotChallenge, ex.Violation);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Waits);
    }

    // ── FetchArticleAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task FetchArticleAsync_TransformersFixture_ParsesAllFields()
    {
        const string slug = "transformers-pinball-tutorial";
        var mdUrl = $"{BaseUrl}/news/{slug}.md";

        var (client, gate, _) = BuildClient(h => h.MapHtml(mdUrl, TransformersMdBody));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        Assert.NotNull(article);

        // Title: editorial headline from H1 — NOT the game name.
        Assert.Equal("Autobots, Transform and Roll Out!", article!.Title);

        // Author: extracted from the by-line link text.
        Assert.Equal("Noah Crable", article.Author);

        // Canonical URL: the bare URL at the end of the .md body.
        Assert.Equal("https://www.kineticist.com/news/transformers-pinball-tutorial", article.CanonicalUrl);

        // GameSlug: the EXACT slug from the "## Related - Game:" link — not the
        // editorial stem ("transformers") and not the inline John Wick link that
        // appears earlier in the body. This is what makes the OPDB join reliable.
        Assert.Equal("transformers-more-than-meets-the-eye", article.GameSlug);

        // Content: non-empty and contains meaningful body text.
        Assert.False(string.IsNullOrWhiteSpace(article.MarkdownContent));
        Assert.Contains("Stern", article.MarkdownContent, StringComparison.Ordinal);
        Assert.Contains("Megatron", article.MarkdownContent, StringComparison.Ordinal);

        // PublishedAt: June 25, 2026, parsed from "· June 25, 2026 ·".
        Assert.NotNull(article.PublishedAt);
        Assert.Equal(2026, article.PublishedAt!.Value.Year);
        Assert.Equal(6, article.PublishedAt!.Value.Month);
        Assert.Equal(25, article.PublishedAt!.Value.Day);

        // Politeness: the .md fetch went through the gate.
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
        Assert.Contains(gate.Acquired, u => u.AbsoluteUri.Contains(".md"));
    }

    [Fact]
    public async Task FetchArticleAsync_MonsterBashFixture_ParsesAuthorAndDate()
    {
        const string slug = "monster-bash-pinball-tutorial";

        var (client, _, _) = BuildClient(h => h
            .MapHtml($"{BaseUrl}/news/{slug}.md", MonsterBashMdBody));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        Assert.NotNull(article);
        Assert.Equal("Rock Monster: Learn to Play Williams Monster Bash Pinball", article!.Title);
        Assert.Equal("James McFatter", article.Author);
        // This fixture has no "## Related - Game:" block, so GameSlug falls back
        // to the editorial-stem derivation (covers the fallback path).
        Assert.Equal("monster-bash", article.GameSlug);
        Assert.Equal("https://www.kineticist.com/news/monster-bash-pinball-tutorial", article.CanonicalUrl);
        Assert.Equal(2025, article.PublishedAt?.Year);
        Assert.Equal(10, article.PublishedAt?.Month);
    }

    [Fact]
    public async Task FetchArticleAsync_HttpFailure_ReturnsNull()
    {
        const string slug = "nonexistent-tutorial";

        var (client, gate, _) = BuildClient(h => h
            .Map($"{BaseUrl}/news/{slug}.md",
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        // HTTP failures log-and-return-null (degrade visibly per Invariant #17).
        Assert.Null(article);

        // Politeness: the failed request still went through the gate (acquire + report).
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task FetchArticleAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        const string slug = "test-tutorial";

        var (client, gate, _) = BuildClient(h => h
            .MapHtml($"{BaseUrl}/news/{slug}.md",
                "# Test\nContent\nhttps://www.kineticist.com/news/test-tutorial"));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        // Politeness abort must propagate — never silently swallowed.
        await Assert.ThrowsAsync<PolitenessException>(
            () => client.FetchArticleAsync(slug, CancellationToken.None));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml"),
    };

    private static HttpResponseMessage TooManyRequests(Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("Too Many Requests"),
        };
        configure?.Invoke(response);
        return response;
    }

    private static (KineticistTutorialsClient Client, AutoAdvancingTimeProvider Time, QueueingHttpMessageHandler Handler)
        BuildClientWithRealGate(PolitenessOptions politeness, Action<QueueingHttpMessageHandler> configureHandler)
    {
        politeness.RespectRobotsTxt = false;
        var time = new AutoAdvancingTimeProvider(new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.Zero));
        var politenessOptions = Options.Create(politeness);
        var gate = new PolitenessGate(
            new RobotsTxtCache(new HttpClient(new QueueingHttpMessageHandler()), politenessOptions, NullLogger<RobotsTxtCache>.Instance),
            new DefaultPerSourcePolitenessResolver(politenessOptions),
            NullLogger<PolitenessGate>.Instance,
            time);

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var client = new KineticistTutorialsClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate,
            politenessOptions,
            Options.Create(new KineticistOptions { BaseUrl = BaseUrl }),
            NullLogger<KineticistTutorialsClient>.Instance);

        return (client, time, handler);
    }

    private static (KineticistTutorialsClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var options = Options.Create(new KineticistOptions { BaseUrl = BaseUrl });
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(BaseUrl),
        };

        var client = new KineticistTutorialsClient(
            httpClient,
            gate,
            Options.Create(new PolitenessOptions()),
            options,
            NullLogger<KineticistTutorialsClient>.Instance);

        return (client, gate, handler);
    }
}
