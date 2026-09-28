using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Ap;

// Pipeline tests for ApBulletinScraper: WordPress discovery → per-game support
// page fetch → bulletin extraction → yield, against a fake politeness gate and
// the captured 2026-09-28 responses (Fixtures/Ap/CAPTURE.md).
public sealed class ApBulletinScraperTests
{
    private const string BaseUrl = "https://www.american-pinball.com";
    private const string SupportLookupUrl = BaseUrl + "/wp-json/wp/v2/pages?slug=support&_fields=id,slug,link,parent";
    private const string ChildPagesUrl = BaseUrl + "/wp-json/wp/v2/pages?parent=2631&per_page=100&page=1&_fields=id,slug,link";
    private const string CategoriesUrl = BaseUrl + "/wp-json/wp/v2/categories?slug=game-page&_fields=id,slug";
    private const string PostsUrl = BaseUrl + "/wp-json/wp/v2/posts?categories=114&per_page=100&page=1&_fields=slug,link";
    private const string HoudiniSupport = "https://americanpinball.com/support/houdini/";
    private const string BarrySupport = "https://americanpinball.com/support/barry-os-bbq-challenge/";

    // The captured child-page list names six game hubs; two are captured. The
    // catalog here is the captured game-page posts narrowed to those two, so
    // the other four hubs are skipped as not-a-game rather than requested.
    private const string TwoGameCatalog = """
        [
          {"slug":"houdini","link":"https://americanpinball.com/houdini/"},
          {"slug":"barry-os-bbq-challenge","link":"https://americanpinball.com/barry-os-bbq-challenge/"}
        ]
        """;

    [Fact]
    public async Task ScrapeAsync_CapturedSupportPages_YieldsHoudiniBulletinPdfsWithPerGameProvenance()
    {
        var logger = new CapturingLogger<ApBulletinScraper>();
        var (scraper, gate, handler) = BuildScraper(h => MapDiscovery(h)
            .MapHtml(HoudiniSupport, ApFixtures.Read("support-houdini.captured.html"))
            .MapHtml(BarrySupport, ApFixtures.Read("support-barry-os-bbq-challenge.captured.html")),
            logger);

        var items = await ScrapeAllAsync(scraper);

        Assert.Equal(10, items.Count);
        Assert.Contains(items, i => i.Link!.FileUrl ==
            "https://48804760.fs1.hubspotusercontent-na1.net/hubfs/48804760/Support%20Files/Service%20Bulletin/Houdini%20-%20Skill%20Shot%20Fix.pdf");
        Assert.Contains(items, i => i.Link!.FileUrl ==
            "https://48804760.fs1.hubspotusercontent-na1.net/hubfs/48804760/Support%20Files/Electrical/Houdini%20-%20Knocker%20Kit%20Installation%20Guide.pdf");
        Assert.All(items, item =>
        {
            Assert.NotNull(item.Link);
            Assert.Null(item.Game);
            Assert.Equal(SourceType.ApBulletinPage, item.SourceType);
            Assert.Equal(HoudiniSupport, item.DiscoveryUrl);
            Assert.Equal("American Pinball Support Page", item.DiscoveryContext);
            Assert.Equal("houdini", item.Link!.GameSlug);
            Assert.EndsWith(".pdf", item.Link.FileUrl, StringComparison.Ordinal);
        });

        Assert.Equal(
            [SupportLookupUrl, ChildPagesUrl, CategoriesUrl, PostsUrl, BarrySupport, HoudiniSupport],
            handler.Requests.Select(u => u.AbsoluteUri).ToArray());
        Assert.Equal(handler.Requests.Select(u => u.AbsoluteUri), gate.Acquired.Select(u => u.AbsoluteUri));
        Assert.Equal(handler.Requests.Select(u => u.AbsoluteUri), gate.Reported.Select(r => r.Url.AbsoluteUri));
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ScrapeAsync_BulletinPdfsMovedOffAllowedHosts_FailsNamingThePageAndHostAndMeters()
    {
        var houdini = ApFixtures.Read("support-houdini.captured.html")
            .Replace("48804760.fs1.hubspotusercontent-na1.net", "files.example.net", StringComparison.Ordinal);
        var logger = new CapturingLogger<ApBulletinScraper>();
        var (scraper, _, handler) = BuildScraper(h => MapDiscovery(h)
            .MapHtml(HoudiniSupport, houdini)
            .MapHtml(BarrySupport, ApFixtures.Read("support-barry-os-bbq-challenge.captured.html")),
            logger);

        using var meter = new FailureMeter();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScrapeAllAsync(scraper));

        Assert.Contains(HoudiniSupport, ex.Message, StringComparison.Ordinal);
        Assert.Contains("files.example.net", ex.Message, StringComparison.Ordinal);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(HoudiniSupport, error.Message, StringComparison.Ordinal);
        Assert.Contains("no_allowed_documents", error.Message, StringComparison.Ordinal);
        Assert.Contains(meter.Observations, o => o.Scraper == "American Pinball Bulletins" && o.Reason == "no_allowed_documents");
        // Every hub was read before failing, so the error lists every broken hub.
        Assert.Contains(handler.Requests, u => u.AbsoluteUri == HoudiniSupport);
        Assert.Contains(handler.Requests, u => u.AbsoluteUri == BarrySupport);
    }

    [Fact]
    public async Task ScrapeAsync_SupportPageWithoutPostCards_FailsAsLayoutDrift()
    {
        var (scraper, _, _) = BuildScraper(h => MapDiscovery(h)
            .MapHtml(HoudiniSupport, "<html><body><h1>Houdini Support Hub</h1></body></html>")
            .MapHtml(BarrySupport, ApFixtures.Read("support-barry-os-bbq-challenge.captured.html")));

        using var meter = new FailureMeter();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScrapeAllAsync(scraper));

        Assert.Contains("no_post_cards", ex.Message, StringComparison.Ordinal);
        Assert.Contains(HoudiniSupport, ex.Message, StringComparison.Ordinal);
        Assert.Contains(meter.Observations, o => o.Reason == "no_post_cards");
    }

    [Fact]
    public async Task ScrapeAsync_SupportPageFetchFails_FailsAfterReportingTheStatusToTheGate()
    {
        var (scraper, gate, _) = BuildScraper(h => MapDiscovery(h)
            .Map(HoudiniSupport, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .MapHtml(BarrySupport, ApFixtures.Read("support-barry-os-bbq-challenge.captured.html")));

        using var meter = new FailureMeter();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScrapeAllAsync(scraper));

        Assert.Contains("fetch_failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains(gate.Reported, r => r.Url.AbsoluteUri == HoudiniSupport && r.Status == HttpStatusCode.InternalServerError);
        Assert.Contains(meter.Observations, o => o.Reason == "fetch_failed");
    }

    [Fact]
    public async Task ScrapeAsync_NoPerGamePagePublishesBulletins_Fails()
    {
        const string barryOnly = """[{"slug":"barry-os-bbq-challenge","link":"https://americanpinball.com/barry-os-bbq-challenge/"}]""";
        var (scraper, _, _) = BuildScraper(h => MapDiscovery(h, barryOnly)
            .MapHtml(BarrySupport, ApFixtures.Read("support-barry-os-bbq-challenge.captured.html")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScrapeAllAsync(scraper));

        Assert.Contains("none publishes a bulletin post", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScrapeAsync_SupportPageMissingFromWordPress_ThrowsBeforeFetchingChildren()
    {
        var (scraper, _, handler) = BuildScraper(h => h.MapJson(SupportLookupUrl, "[]"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScrapeAllAsync(scraper));

        Assert.Contains("slug 'support'", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ScrapeAsync_ChildPageListSpansSeveralPages_RefusesATruncatedSet()
    {
        var (scraper, _, _) = BuildScraper(h => h
            .MapJson(SupportLookupUrl, ApFixtures.Read("support-page-lookup.captured.json"))
            .Map(ChildPagesUrl, _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ApFixtures.Read("support-child-pages.captured.json"), System.Text.Encoding.UTF8, "application/json"),
                };
                response.Headers.TryAddWithoutValidation("X-WP-TotalPages", "2");
                return response;
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScrapeAllAsync(scraper));

        Assert.Contains("truncated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesWithoutAnyRequest()
    {
        var (scraper, gate, handler) = BuildScraper(h => MapDiscovery(h));
        gate.ThrowOnAcquire = new PolitenessException(PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(() => ScrapeAllAsync(scraper));

        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    private static QueueingHttpMessageHandler MapDiscovery(QueueingHttpMessageHandler handler, string gameCatalog = TwoGameCatalog) =>
        handler
            .MapJson(SupportLookupUrl, ApFixtures.Read("support-page-lookup.captured.json"))
            .MapJson(ChildPagesUrl, ApFixtures.Read("support-child-pages.captured.json"))
            .MapJson(CategoriesUrl, ApFixtures.Read("game-page-category.captured.json"))
            .MapJson(PostsUrl, gameCatalog);

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(ApBulletinScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (ApBulletinScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler, ILogger<ApBulletinScraper>? logger = null)
    {
        var options = Options.Create(new ApOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var sitemapClient = new ApSitemapClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApSitemapClient>.Instance);

        var scraper = new ApBulletinScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            sitemapClient,
            gate, politenessOpts, options,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ApBulletinScraper>.Instance);

        return (scraper, gate, handler);
    }

    // Parallel-tolerant capture of pinwiz.scraper.page_extraction_failed_total.
    private sealed class FailureMeter : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ConcurrentBag<(string? Scraper, string? Reason)> Observations { get; } = [];

        public FailureMeter()
        {
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                if (instrument.Name != "pinwiz.scraper.page_extraction_failed_total") return;
                string? scraper = null;
                string? reason = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "scraper") scraper = tag.Value as string;
                    else if (tag.Key == "reason") reason = tag.Value as string;
                }
                Observations.Add((scraper, reason));
            });
            _listener.Start();
            _listener.EnableMeasurementEvents(PinballWizardTelemetry.ScraperPageExtractionFailures);
        }

        public void Dispose() => _listener.Dispose();
    }
}
