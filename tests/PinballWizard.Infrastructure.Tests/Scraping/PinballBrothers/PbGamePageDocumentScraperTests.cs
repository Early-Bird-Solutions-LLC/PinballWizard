using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="PbGamePageDocumentScraper"/>.
/// Exercises the full <see cref="ISourceScraper.ScrapeAsync"/> flow (WP REST
/// discovery with content → per-page PDF extraction → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing <see cref="HttpMessageHandler"/>.
/// Pins behaviour the extractor unit-tests cannot reach: yield order,
/// provenance-field propagation onto <see cref="ScrapedItem"/>,
/// per-page failure isolation, no-documents page skipping, and
/// polite-scraping invariants.
/// </summary>
/// <remarks>
/// Fixtures derived from 2026-06-25 recon of
/// https://www.pinballbrothers.com/wp-json/wp/v2/pages?slug=abba-pinball&amp;_fields=…,content
/// The WP REST endpoint used by this scraper is:
///   /wp-json/wp/v2/pages?per_page=100&amp;page=1&amp;_fields=id,slug,link,parent,modified,title,content
/// </remarks>
public sealed class PbGamePageDocumentScraperTests
{
    private const string BaseUrl = "https://www.pinballbrothers.com";
    private const string FieldsWithContent = "id,slug,link,parent,modified,title,content";
    private const string AbbaRulesheetUrl =
        "https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf";

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ScrapeAsync_AbbaPageWithRulesheetPdf_YieldsLinkWithFullProvenance()
    {
        // ABBA Pinball is the only PB game with a PDF as of 2026-06-25.
        // Fixture reflects the real nectar_btn shortcode encoding.
        const string pagesJson = $$"""
            [
              {
                "id": 1234,
                "slug": "abba-pinball",
                "link": "{{BaseUrl}}/abba-pinball/",
                "parent": 0,
                "modified": "2026-06-25T10:00:00",
                "title": { "rendered": "ABBA Pinball" },
                "content": {
                  "rendered": "[nectar_btn text=\"Rulesheet\" url=\"{{AbbaRulesheetUrl}}\" open_new_tab=\"true\"]"
                }
              }
            ]
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), pagesJson));

        var items = await ScrapeAllAsync(scraper);

        // One PDF from ABBA's game page.
        Assert.Single(items);
        var item = items[0];

        // Item is a Link (document), not a Game.
        Assert.NotNull(item.Link);
        Assert.Null(item.Game);

        // Full provenance.
        Assert.Equal(SourceType.PinballBrothersDocumentPage, item.SourceType);
        Assert.Equal("Pinball Brothers Game Page", item.DiscoveryContext);
        Assert.Equal($"{BaseUrl}/abba-pinball/", item.DiscoveryUrl);

        // Link fields.
        Assert.Equal(AbbaRulesheetUrl, item.Link!.FileUrl);
        Assert.Equal("Rulesheet", item.Link.LinkText);
        Assert.Equal("abba", item.Link.GameSlug);

        // Politeness invariants.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.All(gate.Reported, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Status));
        Assert.Equal(
            handler.Requests.Select(u => u.AbsoluteUri),
            gate.Acquired.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task ScrapeAsync_PredatorPageWithNoDocuments_YieldsNothing()
    {
        // Predator (and Queen, Alien) have no document PDFs as of 2026-06-25.
        // The scraper should skip silently (graceful empty per page).
        const string pagesJson = $$"""
            [
              {
                "id": 5678,
                "slug": "predator-pinball",
                "link": "{{BaseUrl}}/predator-pinball/",
                "parent": 0,
                "modified": "2026-06-20T10:00:00",
                "title": { "rendered": "Predator Pinball" },
                "content": {
                  "rendered": "[nectar_btn text=\"Buy Predator\" url=\"/distributors/\"][nectar_video_lightbox video_url=\"https://www.youtube.com/watch?v=HzH7b1DJ4ZU\"]"
                }
              }
            ]
            """;

        var (scraper, _, _) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), pagesJson));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_MixedPages_YieldsOnlyPagesWithDocuments()
    {
        // ABBA has a PDF; Predator does not.  Only ABBA's item is yielded.
        const string pagesJson = $$"""
            [
              {
                "id": 1234,
                "slug": "abba-pinball",
                "link": "{{BaseUrl}}/abba-pinball/",
                "parent": 0,
                "modified": "2026-06-25T10:00:00",
                "title": { "rendered": "ABBA Pinball" },
                "content": {
                  "rendered": "[nectar_btn text=\"Rulesheet\" url=\"{{AbbaRulesheetUrl}}\"]"
                }
              },
              {
                "id": 5678,
                "slug": "predator-pinball",
                "link": "{{BaseUrl}}/predator-pinball/",
                "parent": 0,
                "modified": "2026-06-20T10:00:00",
                "title": { "rendered": "Predator Pinball" },
                "content": {
                  "rendered": "[nectar_btn text=\"Buy Predator\" url=\"/distributors/\"]"
                }
              }
            ]
            """;

        var (scraper, _, _) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), pagesJson));

        var items = await ScrapeAllAsync(scraper);

        Assert.Single(items);
        Assert.Equal("abba", items[0].Link!.GameSlug);
        Assert.Equal(AbbaRulesheetUrl, items[0].Link!.FileUrl);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // WP REST returns 500.  Scraper must yield nothing and not throw.
        var (scraper, _, _) = BuildScraper(h => h
            .Map(WpPagesUrl(1),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the orchestrator needs
        // to see it to mark the source as aborted.
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), "[]"));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });

        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string WpPagesUrl(int page) =>
        $"{BaseUrl}/wp-json/wp/v2/pages?per_page=100&page={page}&_fields={FieldsWithContent}";

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(PbGamePageDocumentScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (PbGamePageDocumentScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var pbOptions = Options.Create(new PinballBrothersOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var pagesClient = new PbWpPagesClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, pbOptions,
            NullLogger<PbWpPagesClient>.Instance);

        var scraper = new PbGamePageDocumentScraper(
            pagesClient,
            gate, politenessOpts, pbOptions,
            NullLogger<PbGamePageDocumentScraper>.Instance);

        return (scraper, gate, handler);
    }
}
