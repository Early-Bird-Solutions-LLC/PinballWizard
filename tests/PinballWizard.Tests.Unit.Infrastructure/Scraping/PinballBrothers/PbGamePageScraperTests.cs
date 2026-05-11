using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Tests.Unit.Infrastructure.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="PbGamePageScraper"/>.
/// Exercises the full <see cref="Core.Scraping.ISourceScraper.ScrapeAsync"/>
/// flow (WP-REST discovery → per-page extract → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test
/// surface cannot reach: yield order, provenance-field propagation
/// onto <see cref="ScrapedItem"/>, per-page failure isolation, and
/// the polite-scraping invariants (every fetched URL passes through
/// the gate, every response is reported back).
/// </summary>
/// <remarks>
/// Backfill of the PR #41 family-wide template. Pinball Brothers is a
/// SINGLE-yield scraper (only <c>.Game</c>; no <c>.Link</c>) — game
/// pages contain no firmware downloads or other linkable assets.
/// Discovery is paginated WP-REST JSON at
/// <c>/wp-json/wp/v2/pages?per_page=N&amp;page=M</c>; per-page
/// extraction is pure (no HTTP), so per-page failures are surfaced as
/// extractor returning null rather than per-page HTTP failures.
/// </remarks>
public sealed class PbGamePageScraperTests
{
    private const string BaseUrl = "https://www.pinballbrothers.com";
    private const string FieldsParam = "id,slug,link,parent,modified,title";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGamesInPageOrderWithProvenance()
    {
        // Two game pages on a single WP-REST batch. Batch size below
        // PageSize so pagination terminates after one fetch.
        const string pageJson = """
            [
              {
                "id": 1852,
                "slug": "queen-pinball",
                "link": "https://www.pinballbrothers.com/queen-pinball/",
                "parent": 0,
                "modified": "2026-04-01T10:00:00",
                "title": { "rendered": "Queen Pinball" }
              },
              {
                "id": 1730,
                "slug": "alien-pinball",
                "link": "https://www.pinballbrothers.com/alien-pinball/",
                "parent": 0,
                "modified": "2026-03-15T10:00:00",
                "title": { "rendered": "Alien Pinball" }
              }
            ]
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), pageJson));

        var items = await ScrapeAllAsync(scraper);

        // Yield order: pages in the order returned by the WP-REST
        // batch. Single-yield scraper — every item is .Game, no .Link.
        Assert.Equal(2, items.Count);
        Assert.NotNull(items[0].Game);
        Assert.Null(items[0].Link);
        Assert.Equal("Queen Pinball", items[0].Game!.Title);
        Assert.Equal("queen", items[0].Game!.Slug);
        Assert.Equal("game_pinballbrothers_queen", items[0].Game!.GameId);

        Assert.NotNull(items[1].Game);
        Assert.Null(items[1].Link);
        Assert.Equal("Alien Pinball", items[1].Game!.Title);
        Assert.Equal("alien", items[1].Game!.Slug);

        // Provenance propagation: every yielded item carries the
        // discovery URL (the page's published link), the discovery
        // context, and the source-type sentinel.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.PinballBrothersGamePage, item.SourceType);
            Assert.Equal("Pinball Brothers Game Page", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.StartsWith(BaseUrl, item.DiscoveryUrl);
        }

        // Politeness invariants: every fetched URL passed through the
        // gate (acquire + report), every lease was disposed, AND the
        // URL the gate saw is byte-identical to the URL the wire saw —
        // so a future refactor that re-canonicalises between gate and
        // send cannot silently throttle a different origin. This is
        // the load-bearing pin from PR #41 lines 100-109.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.All(gate.Reported, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Status));
        Assert.Equal(
            handler.Requests.Select(u => u.AbsoluteUri),
            gate.Acquired.Select(u => u.AbsoluteUri));
        Assert.Equal(
            handler.Requests.Select(u => u.AbsoluteUri),
            gate.Reported.Select(r => r.Url.AbsoluteUri));

        // Provenance: the GameRecord carries the page URL and the
        // discovery sentinel forward — these are what survive into
        // catalog.json and into Phase 2 RAG citations. Provenance is
        // the project's load-bearing principle; pin it in the template.
        var firstGame = items[0].Game!;
        Assert.Equal("https://www.pinballbrothers.com/queen-pinball/", firstGame.Source!.ScrapedFrom);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("pinballbrothers_wp_pages", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // PB has no per-game HTTP fetch (extraction is pure from the
        // WP-REST payload), so the analogue of CGC's per-page-500
        // failure is "one malformed page in the JSON list returns null
        // from the extractor; siblings still yield." A page whose slug
        // equals the suffix exactly (so the canonical slug is empty)
        // survives FilterGamePages but ExtractGame returns null for it.
        const string pageJson = """
            [
              {
                "id": 1852,
                "slug": "queen-pinball",
                "link": "https://www.pinballbrothers.com/queen-pinball/",
                "parent": 0,
                "modified": "2026-04-01T10:00:00",
                "title": { "rendered": "Queen Pinball" }
              },
              {
                "id": 9999,
                "slug": "-pinball",
                "link": "https://www.pinballbrothers.com/-pinball/",
                "parent": 0,
                "modified": "2026-01-01T10:00:00",
                "title": { "rendered": "Broken" }
              },
              {
                "id": 1730,
                "slug": "alien-pinball",
                "link": "https://www.pinballbrothers.com/alien-pinball/",
                "parent": 0,
                "modified": "2026-03-15T10:00:00",
                "title": { "rendered": "Alien Pinball" }
              }
            ]
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), pageJson));

        var items = await ScrapeAllAsync(scraper);

        // Only the two valid game pages should yield; the malformed
        // one is silently skipped (TryExtract returns null → continue).
        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "queen");
        Assert.Contains(games, i => i.Game!.Slug == "alien");
        Assert.DoesNotContain(games, i => i.Game!.Slug == string.Empty);

        // Politeness invariants must hold on the failure path too. The
        // malformed page never causes an HTTP request (extraction is
        // pure), but the discovery batch did, and that one request
        // must still be reported back to the gate.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // The first WP-REST page returns 500. The scraper must yield
        // nothing AND not throw — the orchestrator handles per-source
        // aborts via the outer try/catch, but this scraper's contract
        // is to yield-break cleanly on discovery failure (the
        // exception filter on the catch in ScrapeAsync excludes
        // PolitenessException and OperationCanceledException).
        var (scraper, _, _) = BuildScraper(h => h
            .Map(WpPagesUrl(1),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the orchestrator
        // needs to see it so the source is marked aborted for the run
        // (and the next scraper still gets to run via the orchestrator's
        // outer try/catch). The scraper-level discovery-failure filter
        // explicitly excludes PolitenessException.
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

        // The throw came from the gate, BEFORE any HTTP request fired —
        // so the wire must show zero requests and the gate must show
        // zero reports. A regression that swallowed the exception and
        // let the run continue would fetch the WP-REST page and
        // possibly more; both are pinned out by these assertions.
        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    [Fact]
    public async Task ScrapeAsync_GateThrowsOnReport_BubblesUp()
    {
        // Symmetric to the acquire-throws case: a politeness violation
        // detected at report time (e.g. 429-streak limit reached) must
        // also propagate. Pinning this exercises the otherwise-untested
        // ReportResponseAsync error path on the gate.
        const string pageJson = """
            [
              {
                "id": 1852,
                "slug": "queen-pinball",
                "link": "https://www.pinballbrothers.com/queen-pinball/",
                "parent": 0,
                "modified": "2026-04-01T10:00:00",
                "title": { "rendered": "Queen Pinball" }
              }
            ]
            """;
        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson(WpPagesUrl(1), pageJson));

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

    private static string WpPagesUrl(int page) =>
        $"{BaseUrl}/wp-json/wp/v2/pages?per_page=100&page={page}&_fields={FieldsParam}";

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(PbGamePageScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (PbGamePageScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
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

        var scraper = new PbGamePageScraper(
            pagesClient,
            gate, politenessOpts, pbOptions,
            NullLogger<PbGamePageScraper>.Instance);

        return (scraper, gate, handler);
    }
}
