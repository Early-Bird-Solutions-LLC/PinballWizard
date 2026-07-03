using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class PbFreshdeskDocumentScraperTests
{
    private const string BaseUrl = "https://pinballbrothers.freshdesk.com";
    private const string SolutionsUrl = $"{BaseUrl}/support/solutions";

    private const string SolutionsHomeHtml = """
        <html><body>
        <section id="solutions-home">
          <div class="cs-s">
            <h3 class="heading"> <a href="/x">General</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list"><div class="list-lead">
                <a href="/support/solutions/folders/1" title="Service Bulletin"> Service Bulletin <span class='item-count'>1</span></a>
              </div></section>
            </div>
          </div>
          <div class="cs-s">
            <h3 class="heading"> <a href="/x">FAQs QUEEN</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list"><div class="list-lead">
                <a href="/support/solutions/folders/2" title="Queen - General"> Queen - General <span class='item-count'>2</span></a>
              </div></section>
            </div>
          </div>
        </section>
        </body></html>
        """;

    private const string ServiceBulletinFolderHtml = """
        <html><body><h2 class="heading">Service Bulletin</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/10-sb1" class="c-link">#001 Drop target bank coil short circuit</a></div></div>
        </section>
        </body></html>
        """;

    private const string QueenGeneralFolderHtml = """
        <html><body><h2 class="heading">Queen - General</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/20-manual" class="c-link">QUEEN Pinball - Technical Manual</a></div></div>
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/21-howto" class="c-link">"How-to" Remove Guitar playfield</a></div></div>
        </section>
        </body></html>
        """;

    private static string ArticleWithAttachment(string title) => $$"""
        <html><body>
        <h2 class="heading">{{title}} <a id="print-article"><span>Print</span></a></h2>
        <article class="article-body" id="article-body"><p>Body text.</p></article>
        <div class="cs-g-c attachments"><div class="attachment"><div class="attach_content"><div class="ellipsis">
            <a href="/helpdesk/attachments/999" class="filename" title='{{title}}.pdf'>truncated...</a>
        </div></div></div></div>
        </body></html>
        """;

    private const string ArticleWithoutAttachmentHtml = """
        <html><body>
        <h2 class="heading">"How-to" Remove Guitar playfield <a id="print-article"><span>Print</span></a></h2>
        <article class="article-body" id="article-body"><p>Step-by-step instructions.</p></article>
        </body></html>
        """;

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsOnlyAttachmentBearingArticlesWithProvenance()
    {
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml(SolutionsUrl, SolutionsHomeHtml)
            .MapHtml($"{BaseUrl}/support/solutions/folders/1", ServiceBulletinFolderHtml)
            .MapHtml($"{BaseUrl}/support/solutions/folders/2", QueenGeneralFolderHtml)
            .MapHtml($"{BaseUrl}/support/solutions/articles/10-sb1", ArticleWithAttachment("#001 Drop target bank coil short circuit"))
            .MapHtml($"{BaseUrl}/support/solutions/articles/20-manual", ArticleWithAttachment("QUEEN Pinball - Technical Manual"))
            .MapHtml($"{BaseUrl}/support/solutions/articles/21-howto", ArticleWithoutAttachmentHtml));

        var items = await ScrapeAllAsync(scraper);

        // Only the two attachment-bearing articles yield. The text-only
        // "How-to" article is skipped entirely — it belongs to the
        // synthesizer path (Task 7/8), not this scraper.
        Assert.Equal(2, items.Count);

        var bulletin = items.Single(i => i.Link!.LinkText == "#001 Drop target bank coil short circuit");
        Assert.Null(bulletin.Link!.GameSlug); // "General" category → no specific machine.
        Assert.Equal("Freshdesk Support Portal — Service Bulletin", bulletin.DiscoveryContext);
        Assert.Equal($"{BaseUrl}/support/solutions/articles/10-sb1", bulletin.DiscoveryUrl);
        Assert.Equal(SourceType.PinballBrothersFreshdeskArticle, bulletin.SourceType);

        var manual = items.Single(i => i.Link!.LinkText == "QUEEN Pinball - Technical Manual");
        Assert.Equal("queen", manual.Link!.GameSlug); // "FAQs QUEEN" category → "queen".
        Assert.Equal("Freshdesk Support Portal — Queen - General", manual.DiscoveryContext);
        Assert.Equal($"{BaseUrl}/helpdesk/attachments/999", manual.Link.FileUrl);

        // Politeness invariants across the whole discovery + fetch chain:
        // homepage (1) + 2 folder pages + 3 article fetches = 6 requests.
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_ArticleFetchFailure_DoesNotAbortRun()
    {
        var (scraper, _, _) = BuildScraper(h => h
            .MapHtml(SolutionsUrl, SolutionsHomeHtml)
            .Map($"{BaseUrl}/support/solutions/folders/1",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{BaseUrl}/support/solutions/folders/2", QueenGeneralFolderHtml)
            .MapHtml($"{BaseUrl}/support/solutions/articles/20-manual", ArticleWithAttachment("QUEEN Pinball - Technical Manual"))
            .MapHtml($"{BaseUrl}/support/solutions/articles/21-howto", ArticleWithoutAttachmentHtml));

        var items = await ScrapeAllAsync(scraper);

        // The Service Bulletin folder's discovery failed (500), but the
        // Queen folder's manual still yields — one folder's failure must
        // not abort the whole run.
        Assert.Single(items);
        Assert.Equal("QUEEN Pinball - Technical Manual", items[0].Link!.LinkText);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_YieldsNothingAndDoesNotThrow()
    {
        var (scraper, _, _) = BuildScraper(h => h
            .Map(SolutionsUrl, _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        var (scraper, gate, _) = BuildScraper(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));
        gate.ThrowOnAcquire = new PolitenessException(PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public void Name_And_SourceId_MatchCanonicalRegistrations()
    {
        var (scraper, _, _) = BuildScraper(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));

        Assert.Equal("Pinball Brothers Freshdesk Documents", scraper.Name);
        Assert.Equal("pb_freshdesk", scraper.SourceId);
        Assert.Equal("Pinball Brothers", scraper.Manufacturer);
    }

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(PbFreshdeskDocumentScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None)) items.Add(item);
        return items;
    }

    private static (PbFreshdeskDocumentScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var freshdeskOptions = Options.Create(new FreshdeskOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) };

        var client = new FreshdeskSolutionsClient(
            httpClient, gate, politenessOpts, freshdeskOptions,
            NullLogger<FreshdeskSolutionsClient>.Instance);

        var scraper = new PbFreshdeskDocumentScraper(
            client, gate, politenessOpts,
            NullLogger<PbFreshdeskDocumentScraper>.Instance);

        return (scraper, gate, handler);
    }
}
