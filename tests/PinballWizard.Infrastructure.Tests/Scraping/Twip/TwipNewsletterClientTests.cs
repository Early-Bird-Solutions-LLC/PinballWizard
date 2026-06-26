using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Twip;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Twip;

public sealed class TwipNewsletterClientTests
{
    private const string BaseUrl = "https://twip.kineticist.com";
    private const string SitemapUrl = $"{BaseUrl}/sitemap.xml";

    // ── Inline fixtures ─────────────────────────────────────────────────

    private const string SitemapXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url>
            <loc>https://twip.kineticist.com/p/this-week-2026-06-20</loc>
            <lastmod>2026-06-20</lastmod>
          </url>
          <url>
            <loc>https://twip.kineticist.com/p/this-week-2026-06-13</loc>
            <lastmod>2026-06-13</lastmod>
          </url>
          <url>
            <loc>https://twip.kineticist.com/p/this-week-2026-05-01</loc>
            <lastmod>2026-05-01</lastmod>
          </url>
          <url>
            <loc>https://twip.kineticist.com/archive</loc>
            <lastmod>2026-06-20</lastmod>
          </url>
          <url>
            <loc>https://twip.kineticist.com/authors/colin-alsheimer</loc>
            <lastmod>2026-06-20</lastmod>
          </url>
        </urlset>
        """;

    // Represents a real TWIP article page (static HTML; no JS rendering needed).
    private const string ArticleHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <title>This Week in Pinball: June 20, 2026</title>
          <script type="application/ld+json">
          {
            "@type": "Article",
            "@context": "https://schema.org",
            "headline": "This Week in Pinball: June 20, 2026",
            "description": "Stern announces new title; JJP updates pricing.",
            "datePublished": "2026-06-20T08:00:00Z",
            "author": {"@type": "Person", "name": "Colin Alsheimer"},
            "url": "https://twip.kineticist.com/p/this-week-2026-06-20"
          }
          </script>
        </head>
        <body>
          <h2 class="dream-post-content-h2">New Releases</h2>
          <p class="dream-post-content-paragraph">Stern Pinball announced a new title this week.</p>
          <p class="dream-post-content-paragraph">JJP has updated pricing on existing titles.</p>
        </body>
        </html>
        """;

    // A page without JSON-LD (edge case — e.g. subscribe or archive page).
    private const string NoJsonLdHtml = """
        <!DOCTYPE html><html><head><title>Subscribe</title></head>
        <body><p>Subscribe to our newsletter.</p></body></html>
        """;

    // ── DiscoverArticleSlugsAsync ────────────────────────────────────────

    [Fact]
    public async Task DiscoverArticleSlugsAsync_NoSinceFilter_ReturnsAllArticleSlugs()
    {
        var (client, gate, handler) = BuildClient(h => h.MapXml(SitemapUrl, SitemapXml));

        // null since → returns all /p/ entries regardless of date.
        var slugs = await client.DiscoverArticleSlugsAsync(null, CancellationToken.None);

        Assert.Equal(3, slugs.Count);
        Assert.Contains("this-week-2026-06-20", slugs);
        Assert.Contains("this-week-2026-06-13", slugs);
        Assert.Contains("this-week-2026-05-01", slugs);

        // Non-article paths excluded.
        Assert.DoesNotContain("archive", slugs);
        Assert.DoesNotContain("authors/colin-alsheimer", slugs);

        // Politeness: sitemap fetch went through the gate.
        Assert.Single(handler.Requests);
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(gate.Acquired.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverArticleSlugsAsync_WithSinceFilter_ExcludesOlderArticles()
    {
        var (client, _, _) = BuildClient(h => h.MapXml(SitemapUrl, SitemapXml));

        var since = new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);
        var slugs = await client.DiscoverArticleSlugsAsync(since, CancellationToken.None);

        // Only June 20 is >= June 14. June 13 and May 1 are excluded.
        Assert.Single(slugs);
        Assert.Contains("this-week-2026-06-20", slugs);
    }

    [Fact]
    public async Task DiscoverArticleSlugsAsync_RespectsMaxArticlesToFetch()
    {
        var (client, _, _) = BuildClient(h => h.MapXml(SitemapUrl, SitemapXml),
            maxArticles: 2);

        var slugs = await client.DiscoverArticleSlugsAsync(null, CancellationToken.None);

        // Cap at 2 even though sitemap has 3 article entries.
        Assert.Equal(2, slugs.Count);
    }

    [Fact]
    public async Task DiscoverArticleSlugsAsync_NoDuplicateSlugs()
    {
        var (client, _, _) = BuildClient(h => h.MapXml(SitemapUrl, SitemapXml));

        var slugs = await client.DiscoverArticleSlugsAsync(null, CancellationToken.None);

        Assert.Equal(slugs.Count, slugs.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
    }

    [Fact]
    public async Task DiscoverArticleSlugsAsync_SitemapHttpFailure_ThrowsHttpRequestException()
    {
        var (client, _, _) = BuildClient(h => h.Map(SitemapUrl,
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        // HTTP failures from sitemap fetch propagate — caller decides retry.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DiscoverArticleSlugsAsync(null, CancellationToken.None));
    }

    // ── FetchArticleAsync ────────────────────────────────────────────────

    [Fact]
    public async Task FetchArticleAsync_FullFixture_ExtractsAllJsonLdFields()
    {
        const string slug = "this-week-2026-06-20";
        var (client, gate, handler) = BuildClient(h =>
            h.MapHtml($"{BaseUrl}/p/{slug}", ArticleHtml));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        Assert.NotNull(article);
        Assert.Equal("this-week-2026-06-20", article!.Slug);
        Assert.Equal("This Week in Pinball: June 20, 2026", article.Title);
        Assert.Equal("Stern announces new title; JJP updates pricing.", article.Description);
        Assert.Equal("Colin Alsheimer", article.Author);
        Assert.Equal("https://twip.kineticist.com/p/this-week-2026-06-20", article.CanonicalUrl);
        Assert.NotNull(article.PublishedAt);
        Assert.Equal(2026, article.PublishedAt!.Value.Year);
        Assert.Equal(6, article.PublishedAt!.Value.Month);
        Assert.Equal(20, article.PublishedAt!.Value.Day);

        // Politeness: exactly one request went through the gate.
        Assert.Single(handler.Requests);
        Assert.Equal(gate.Acquired.Count, gate.Reported.Count);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task FetchArticleAsync_FullFixture_ExtractsBodyText()
    {
        const string slug = "this-week-2026-06-20";
        var (client, _, _) = BuildClient(h =>
            h.MapHtml($"{BaseUrl}/p/{slug}", ArticleHtml));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        Assert.NotNull(article);
        Assert.False(string.IsNullOrWhiteSpace(article!.BodyText));
        Assert.Contains("Stern Pinball announced", article.BodyText, StringComparison.Ordinal);
        Assert.Contains("JJP has updated pricing", article.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchArticleAsync_BodyTextContainsHeadings()
    {
        const string slug = "this-week-2026-06-20";
        var (client, _, _) = BuildClient(h =>
            h.MapHtml($"{BaseUrl}/p/{slug}", ArticleHtml));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        // Heading "New Releases" from h2.dream-post-content-h2 must appear in body.
        Assert.NotNull(article);
        Assert.Contains("New Releases", article!.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchArticleAsync_NoJsonLd_ReturnsNull()
    {
        const string slug = "subscribe";
        var (client, _, _) = BuildClient(h =>
            h.MapHtml($"{BaseUrl}/p/{slug}", NoJsonLdHtml));

        // Pages without Article JSON-LD degrade visibly (null + logged warning).
        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        Assert.Null(article);
    }

    [Fact]
    public async Task FetchArticleAsync_HttpFailure_ReturnsNull()
    {
        const string slug = "nonexistent";
        var (client, gate, _) = BuildClient(h => h.Map($"{BaseUrl}/p/{slug}",
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var article = await client.FetchArticleAsync(slug, CancellationToken.None);

        // HTTP failures log-and-return-null (degrade visibly per Invariant #17).
        Assert.Null(article);
        // Politeness: failed request still went through gate.
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task FetchArticleAsync_PolitenessException_PropagatesUp()
    {
        const string slug = "test-article";
        var (client, gate, _) = BuildClient(h =>
            h.MapHtml($"{BaseUrl}/p/{slug}", ArticleHtml));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(
            () => client.FetchArticleAsync(slug, CancellationToken.None));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static (TwipNewsletterClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler, int maxArticles = 500)
    {
        var options = Options.Create(new TwipOptions
        {
            BaseUrl = BaseUrl,
            SitemapPath = "/sitemap.xml",
            DefaultLookbackDays = 14,
            MaxArticlesToFetch = maxArticles,
        });
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(BaseUrl),
        };

        var client = new TwipNewsletterClient(
            httpClient,
            gate,
            Options.Create(new PolitenessOptions()),
            options,
            NullLogger<TwipNewsletterClient>.Instance);

        return (client, gate, handler);
    }
}
