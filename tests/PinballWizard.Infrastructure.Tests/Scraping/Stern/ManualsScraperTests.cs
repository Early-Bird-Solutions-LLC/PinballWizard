using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Stern;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="ManualsScraper"/>.
/// Exercises the full <see cref="Core.Scraping.ISourceScraper.ScrapeAsync"/>
/// flow (manuals-page fetch → AngleSharp anchor walk → yield) against a
/// fake <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test
/// surface cannot reach: yield order, provenance-field propagation
/// onto <see cref="ScrapedItem"/>, per-link extraction failure
/// isolation, and the polite-scraping invariants (every fetched URL
/// passes through the gate, every response is reported back).
/// </summary>
/// <remarks>
/// First non-Game-yielding scraper backfilled with the PR #41
/// template. <see cref="ManualsScraper"/> emits <c>.Link</c> items
/// only — manuals are documents, not games — so there are no
/// <c>.Game</c> assertions, no <c>.Link.GameSlug</c> parent-lineage
/// (the scraper does not extract slugs from filenames), and the
/// happy-path fixture asserts <c>.Link</c> yield order with full
/// provenance instead of the CGC template's interleaved game/link
/// ordering. There is also no per-page HTTP fetch step: the entire
/// scrape is a single HTTP GET against <c>/manuals/</c> followed by
/// inline anchor parsing, so the failure-isolation test pins
/// "one malformed anchor doesn't abort the rest" rather than
/// CGC's "one per-page 500 doesn't abort the rest".
/// </remarks>
public sealed class ManualsScraperTests
{
    private const string BaseUrl = "https://sternpinball.com";
    private const string ManualsUrl = $"{BaseUrl}/manuals/";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsLinksInPageOrderWithProvenance()
    {
        // Three same-host PDFs in a fixed order, plus two anchors
        // that must be filtered out: a non-PDF page link and an
        // off-host PDF. Pinning these in the same fixture exercises
        // both of the scraper's filter conditions.
        const string indexHtml = """
            <html><body>
              <a href="/manuals/Stranger_Things_Manual.pdf">Stranger Things Manual</a>
              <a href="/about">About</a>
              <a href="https://sternpinball.com/manuals/Jaws_Manual.pdf">Jaws Manual</a>
              <a href="https://example.com/manuals/Other_Manual.pdf">Off-host PDF</a>
              <a href="/manuals/Foo_Fighters_Manual.pdf">Foo Fighters Manual</a>
            </body></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h.MapHtml(ManualsUrl, indexHtml));

        var items = await ScrapeAllAsync(scraper);

        // Yield order: same-host PDFs in the order they appear on
        // the page; the page-link and off-host PDF are filtered out.
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Link));
        Assert.All(items, i => Assert.Null(i.Game));

        Assert.Equal($"{BaseUrl}/manuals/Stranger_Things_Manual.pdf", items[0].Link!.FileUrl);
        Assert.Equal("Stranger Things Manual", items[0].Link!.LinkText);
        Assert.Equal($"{BaseUrl}/manuals/Jaws_Manual.pdf", items[1].Link!.FileUrl);
        Assert.Equal($"{BaseUrl}/manuals/Foo_Fighters_Manual.pdf", items[2].Link!.FileUrl);

        // GameSlug stays null: ManualsScraper does NOT derive game
        // slugs from filenames. Cross-linking manuals to games
        // happens later in the catalog-merge step (or, per the
        // project's known-gap list, doesn't happen at all yet).
        Assert.All(items, i => Assert.Null(i.Link!.GameSlug));

        // Provenance propagation: every yielded item carries the
        // discovery URL, the discovery context, and the source-type
        // sentinel. Both the per-Link DiscoveryContext AND the
        // outer ScrapedItem.DiscoveryContext are pinned because
        // catalog-builder consumers read both.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.ManualsPage, item.SourceType);
            Assert.Equal("Manuals Page", item.DiscoveryContext);
            Assert.Equal(ManualsUrl, item.DiscoveryUrl);
            Assert.Equal("Manuals Page", item.Link!.DiscoveryContext);
        }

        // Politeness invariants: only ONE HTTP request fires (the
        // manuals page itself — there are no per-link fetches in
        // this scraper) and that request flows through the gate
        // (acquire + report), the lease is disposed, AND the URL
        // the gate saw is byte-identical to the URL the wire saw —
        // so a future refactor that re-canonicalises between gate
        // and send cannot silently throttle a different origin.
        Assert.Single(handler.Requests);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
        Assert.Equal(System.Net.HttpStatusCode.OK, gate.Reported[0].Status);
        Assert.Equal(handler.Requests[0].AbsoluteUri, gate.Acquired[0].AbsoluteUri);
        Assert.Equal(handler.Requests[0].AbsoluteUri, gate.Reported[0].Url.AbsoluteUri);
        Assert.Equal(ManualsUrl, handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task ScrapeAsync_OneAnchorWithMissingHref_DoesNotAbortRun()
    {
        // ManualsScraper has no per-link HTTP fetch step (the
        // catalog-build / file-download stages happen later in the
        // orchestrator), so the per-item failure mode is at the
        // anchor-parsing layer, not the network layer. A malformed
        // anchor — empty href, whitespace href, off-host href, or
        // non-PDF href — must skip that anchor without aborting
        // sibling anchors. The fixture mixes all four rejection
        // reasons between two valid PDFs to pin that the loop
        // continues past every filter branch.
        const string indexHtml = """
            <html><body>
              <a href="/manuals/Pulp_Fiction_Manual.pdf">Pulp Fiction Manual</a>
              <a href="">Empty href</a>
              <a href="   ">Whitespace href</a>
              <a href="https://example.com/manuals/Other.pdf">Off-host PDF</a>
              <a href="/games/about">Page link, not a PDF</a>
              <a href="/manuals/Bond_Manual.pdf">Bond Manual</a>
            </body></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h.MapHtml(ManualsUrl, indexHtml));

        var items = await ScrapeAllAsync(scraper);

        // The two valid PDFs survive in original page order. The
        // four rejected anchors don't break the loop or cause a
        // throw — pinning per-link extraction failure isolation.
        Assert.Equal(2, items.Count);
        Assert.Equal($"{BaseUrl}/manuals/Pulp_Fiction_Manual.pdf", items[0].Link!.FileUrl);
        Assert.Equal($"{BaseUrl}/manuals/Bond_Manual.pdf", items[1].Link!.FileUrl);

        // Politeness invariants still hold under the rejection mix:
        // exactly one fetch (the manuals page), one acquire, one
        // report, one lease disposed.
        Assert.Single(handler.Requests);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // The manuals page itself fails. The scraper must yield
        // nothing AND not throw — the orchestrator handles
        // per-source aborts via the outer try/catch around
        // ScrapeAsync(), but this scraper's contract is to
        // yield-break cleanly on discovery failure. The catch in
        // ManualsScraper is scoped to HttpRequestException, which
        // is what EnsureSuccessStatusCode raises on a 500.
        var (scraper, gate, handler) = BuildScraper(h => h
            .Map(ManualsUrl,
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);

        // Politeness invariants must hold on the failure path too —
        // the 500 response must still be reported back so the
        // 429-streak detector can see real failures, and the lease
        // must be disposed.
        Assert.Single(handler.Requests);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, gate.Reported[0].Status);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the
        // orchestrator needs to see it so the source is marked
        // aborted for the run (and the next scraper still gets to
        // run via the orchestrator's outer try/catch). The
        // scraper-level discovery-failure filter is scoped to
        // HttpRequestException so PolitenessException propagates
        // naturally past it.
        var (scraper, gate, handler) = BuildScraper(h => h.MapHtml(ManualsUrl, "<html/>"));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });

        // The throw came from the gate, BEFORE any HTTP request
        // fired — so the wire must show zero requests and the
        // gate must show zero reports. A regression that
        // swallowed the exception and let the run continue would
        // fetch /manuals/; that is pinned out here.
        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    [Fact]
    public async Task ScrapeAsync_GateThrowsOnReport_BubblesUp()
    {
        // Symmetric to the acquire-throws case: a politeness
        // violation detected at report time (e.g. 429-streak limit
        // reached) must also propagate. Pinning this exercises the
        // otherwise-untested ReportResponseAsync error path on the
        // gate.
        const string indexHtml = """
            <html><body>
              <a href="/manuals/Stranger_Things_Manual.pdf">Stranger Things</a>
            </body></html>
            """;
        var (scraper, gate, _) = BuildScraper(h => h.MapHtml(ManualsUrl, indexHtml));

        gate.ThrowOnReport = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "report-side");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });
    }

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(ManualsScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (ManualsScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var scraperSettings = Options.Create(new ScraperSettings { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var scraper = new ManualsScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate,
            politenessOpts,
            scraperSettings,
            NullLogger<ManualsScraper>.Instance);

        return (scraper, gate, handler);
    }
}
