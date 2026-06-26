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
    private const string CategoryPath = "/news/category/pinball-tutorial";

    // ── Inline fixtures ─────────────────────────────────────────────────────────

    // Captures the article link regex (must match /news/{slug} hrefs that end in
    // tutorial/rules/guide/strategy/pinball keywords but not category/author/tag).
    private const string CategoryPage1Html = """
        <!DOCTYPE html>
        <html>
        <body>
          <article>
            <a href="/news/transformers-pinball-tutorial">Autobots, Transform and Roll Out!</a>
          </article>
          <article>
            <a href="/news/monster-bash-pinball-tutorial">Rock Monster: Learn to Play Williams Monster Bash Pinball</a>
          </article>
          <nav class="pagination">
            <a href="/news/category/pinball-tutorial?page=2">Next</a>
          </nav>
        </body>
        </html>
        """;

    private const string CategoryPage2Html = """
        <!DOCTYPE html>
        <html>
        <body>
          <article>
            <a href="/news/godzilla-pinball-tutorial">Go, Go, Godzilla! Basic Strategy for a Modern Pinball Classic</a>
          </article>
        </body>
        </html>
        """;

    // Real .md body for Transformers (representative of the actual Kineticist format
    // probed 2026-06-25): H1 title, "by [Author](/author/...) · Date · [Category]", blockquote, body.
    private const string TransformersMdBody = """
        # Autobots, Transform and Roll Out!

        by [Noah Crable](/author/noah-crable) · June 25, 2026 · [Pinball Tutorial](/news/category/pinball-tutorial)

        > Learn to play Stern Pinball's 2026 release, Transformers: More Than Meets the Eye in our latest tutorial.

        ## About Transformers: More Than Meets the Eye

        The Autobots and Decepticons have waged war across the galaxy for eons.

        - **Manufacturer:** Stern
        - **Release Year:** 2026

        ## Getting Started

        Shoot the Megatron scoop to start a mission. Two missions lights One Shall Fall.

        ### Skill Shot

        Plunge the ball softly to the upper flipper and hit any lit shot. Max value: 8M.

        ## Strategies

        Focus on Autobot Run or Prime Target first, then qualify Transformers Multiball.

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

    // ── DiscoverTutorialSlugsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_TwoCategoryPages_ReturnsAllSlugs()
    {
        var (client, gate, handler) = BuildClient(h => h
            .MapHtml(CategoryUrl(1), CategoryPage1Html)
            .MapHtml(CategoryUrl(2), CategoryPage2Html)
            .Map(CategoryUrl(3), _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var slugs = await client.DiscoverTutorialSlugsAsync(CancellationToken.None);

        Assert.Equal(3, slugs.Count);
        Assert.Contains("transformers-pinball-tutorial", slugs);
        Assert.Contains("monster-bash-pinball-tutorial", slugs);
        Assert.Contains("godzilla-pinball-tutorial", slugs);

        // Politeness: every category page fetch went through the gate.
        // Page 1 and 2 fetched; page 3 returns 404 → pagination stops.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_SinglePage_StopsOnEmptyPage()
    {
        // Page 2 returns HTML with no matching article links → stops pagination.
        const string emptyPage = "<html><body><p>No more tutorials.</p></body></html>";

        var (client, gate, _) = BuildClient(h => h
            .MapHtml(CategoryUrl(1), CategoryPage1Html)
            .MapHtml(CategoryUrl(2), emptyPage));

        var slugs = await client.DiscoverTutorialSlugsAsync(CancellationToken.None);

        Assert.Equal(2, slugs.Count);
        Assert.Contains("transformers-pinball-tutorial", slugs);
        Assert.Contains("monster-bash-pinball-tutorial", slugs);

        // Politeness invariants hold even on the empty-page early-exit path.
        Assert.Equal(gate.Acquired.Count, gate.Reported.Count);
        Assert.Equal(gate.Acquired.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverTutorialSlugsAsync_NoDuplicateSlugs_AcrossPages()
    {
        // Page 2 returns no new slugs → dedup kicks in, loop terminates.
        var (client, _, _) = BuildClient(h => h
            .MapHtml(CategoryUrl(1), CategoryPage1Html)
            .Map(CategoryUrl(2), _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var slugs = await client.DiscoverTutorialSlugsAsync(CancellationToken.None);

        // 2 unique slugs from page 1 only.
        Assert.Equal(2, slugs.Count);
        Assert.Equal(slugs.Count, slugs.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
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

        // GameSlug: derived by stripping "-pinball-tutorial" suffix.
        Assert.Equal("transformers", article.GameSlug);

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

    private static string CategoryUrl(int page) => page == 1
        ? $"{BaseUrl}{CategoryPath}"
        : $"{BaseUrl}{CategoryPath}?page={page}";

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
