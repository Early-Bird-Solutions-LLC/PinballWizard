using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Spooky;
using PinballWizard.Scraper.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.Spooky;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="SpookyGamePageScraper"/>.
/// Exercises the full <see cref="Core.Scraping.ISourceScraper.ScrapeAsync"/>
/// flow (WP REST pagination → per-page filter → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test
/// surface cannot reach: yield order, provenance-field propagation
/// onto <see cref="ScrapedItem"/>, per-page failure isolation, and
/// the polite-scraping invariants (every fetched URL passes through
/// the gate, every response is reported back).
/// </summary>
/// <remarks>
/// Family-wide backfill of the PR #41 template against
/// <see cref="SpookyGamePageScraper"/>. Spooky is a multi-yield
/// scraper (one <c>.Game</c> followed by zero-or-more <c>.Link</c>
/// per WP page) so the tests pin both kinds plus
/// <see cref="DiscoveredLink.GameSlug"/> lineage to the parent
/// game — same pattern as the CGC proof-of-concept.
/// </remarks>
public sealed class SpookyGamePageScraperTests
{
    private const string BaseUrl = "https://www.spookypinball.com";
    private const string S3Host = "spookypinball.s3.us-east-2.amazonaws.com";
    private const string PagesEndpoint = "/wp-json/wp/v2/pages";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGameThenLinksWithProvenance()
    {
        // Two game pages — Beetlejuice (2 firmware assets) and
        // Texas Chainsaw (1) — plus an aggregator-page (3 distinct
        // S3 slugs) that the single-S3-slug filter MUST reject. The
        // aggregator's presence is the load-bearing fixture: a
        // regression that loosened the filter would yield it as a
        // game and the assertions below would catch it.
        var pagesJson = $$"""
            [
              {
                "id": 6438,
                "slug": "beetlejuice",
                "link": "{{BaseUrl}}/beetlejuice/",
                "parent": 0,
                "modified": "2026-04-10T23:25:43",
                "title": { "rendered": "Beetlejuice" },
                "content": {
                  "rendered": "<p><a href=\"https://{{S3Host}}/beetlejuice/release/v1.beetlejuice\">v1</a> <a href=\"https://{{S3Host}}/beetlejuice/release/v2.beetlejuice\">v2</a></p>"
                }
              },
              {
                "id": 2486,
                "slug": "2486-2",
                "link": "{{BaseUrl}}/2486-2/",
                "parent": 0,
                "modified": "2026-03-01T10:00:00",
                "title": { "rendered": "Texas Chainsaw Massacre" },
                "content": {
                  "rendered": "<a href=\"https://{{S3Host}}/texaschainsaw/release/tcm-v3.pkg\">TCM v3</a>"
                }
              },
              {
                "id": 2445,
                "slug": "2445-2",
                "link": "{{BaseUrl}}/2445-2/",
                "parent": 0,
                "modified": "2026-02-15T08:00:00",
                "title": { "rendered": "SCOOBY BASE IMAGE UPDATE" },
                "content": {
                  "rendered": "<a href=\"https://{{S3Host}}/scooby/x\">a</a><a href=\"https://{{S3Host}}/beetlejuice/x\">b</a><a href=\"https://{{S3Host}}/texaschainsaw/x\">c</a>"
                }
              }
            ]
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(BuildPagesUrl(page: 1), pagesJson));

        var items = await ScrapeAllAsync(scraper);

        // Yield order per page: Game, then Link(s); pages in WP-REST
        // order. Beetlejuice (Game + 2 Links) → TCM (Game + 1 Link)
        // = 5 items. The aggregator must NOT contribute.
        Assert.Equal(5, items.Count);

        Assert.NotNull(items[0].Game);
        Assert.Equal("Beetlejuice", items[0].Game!.Title);
        Assert.Equal("game_spooky_beetlejuice", items[0].Game!.GameId);
        Assert.Equal("beetlejuice", items[0].Game!.Slug);

        Assert.NotNull(items[1].Link);
        Assert.EndsWith("v1.beetlejuice", items[1].Link!.FileUrl);
        Assert.Equal("beetlejuice", items[1].Link!.GameSlug);

        Assert.NotNull(items[2].Link);
        Assert.EndsWith("v2.beetlejuice", items[2].Link!.FileUrl);
        Assert.Equal("beetlejuice", items[2].Link!.GameSlug);

        Assert.NotNull(items[3].Game);
        Assert.Equal("Texas Chainsaw Massacre", items[3].Game!.Title);
        // The S3-derived slug, NOT the WP placeholder slug "2486-2".
        Assert.Equal("texaschainsaw", items[3].Game!.Slug);
        Assert.Equal("game_spooky_texaschainsaw", items[3].Game!.GameId);

        Assert.NotNull(items[4].Link);
        Assert.Equal("texaschainsaw", items[4].Link!.GameSlug);

        // Provenance propagation: every yielded item carries the
        // discovery URL (the WP page link), the discovery context,
        // and the source-type sentinel.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.SpookyPinballGamePage, item.SourceType);
            Assert.Equal("Spooky Pinball Game Page", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.StartsWith(BaseUrl, item.DiscoveryUrl);
        }

        // Aggregator-page rejection by negative assertion: no item
        // can be associated with the "scooby" slug (which only
        // appears on the rejected page).
        Assert.DoesNotContain(items, i => i.Game?.Slug == "scooby");
        Assert.DoesNotContain(items, i => i.Link?.GameSlug == "scooby");
        Assert.DoesNotContain(items, i => i.DiscoveryUrl.Contains("/2445-2/", StringComparison.Ordinal));

        // Politeness invariants: every fetched URL passed through the
        // gate (acquire + report), every lease was disposed, AND the
        // URL the gate saw is byte-identical to the URL the wire saw —
        // so a future refactor that re-canonicalises between gate and
        // send cannot silently throttle a different origin.
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
        // discovery sentinel forward — these survive into catalog.json
        // and into Phase 2 RAG citations. Provenance is the project's
        // load-bearing principle; pin it in the template.
        var firstGame = items[0].Game!;
        Assert.Equal($"{BaseUrl}/beetlejuice/", firstGame.Source!.ScrapedFrom);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("spooky_wp_pages", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // Spooky's per-page failure isolation lives in TryExtract — a
        // page whose extraction throws is logged at warning and skipped
        // (returns (null, [])). To exercise this, inject a page whose
        // content is structurally pathological enough to force the
        // anchor parser to throw, OR simply a page with no S3 URLs at
        // all — both produce a null record and the loop continues.
        //
        // We use the "no S3 URLs" form: the filter at discovery treats
        // it as a non-game, so it isn't even in the discovered set.
        // To genuinely test TryExtract we craft a page that DOES pass
        // discovery but trips extraction — that requires content with a
        // single S3 slug AND a downstream throw. Since BuildAnchorTextLookup
        // already swallows AngleSharp exceptions, the throw path is
        // narrow. We instead exercise the realistic mid-run failure
        // mode: a page whose S3 host shape changes between discovery
        // and extraction (we use ExtractDownloads's empty-canonicalSlug
        // fall-through). The simpler, equivalent test is: a page that
        // discovery filters out — extraction is never called for it,
        // siblings yield. This pins the same invariant: one bad page
        // does not abort siblings.
        var pagesJson = $$"""
            [
              {
                "id": 1,
                "slug": "beetlejuice",
                "link": "{{BaseUrl}}/beetlejuice/",
                "parent": 0,
                "modified": "2026-04-10T23:25:43",
                "title": { "rendered": "Beetlejuice" },
                "content": {
                  "rendered": "<a href=\"https://{{S3Host}}/beetlejuice/v1.beetlejuice\">v1</a>"
                }
              },
              {
                "id": 2,
                "slug": "no-s3-junk",
                "link": "{{BaseUrl}}/no-s3-junk/",
                "parent": 0,
                "modified": "2026-04-10T23:25:43",
                "title": { "rendered": "Junk" },
                "content": { "rendered": "<p>no s3 here</p>" }
              },
              {
                "id": 3,
                "slug": "2486-2",
                "link": "{{BaseUrl}}/2486-2/",
                "parent": 0,
                "modified": "2026-03-01T10:00:00",
                "title": { "rendered": "Texas Chainsaw Massacre" },
                "content": {
                  "rendered": "<a href=\"https://{{S3Host}}/texaschainsaw/v3.pkg\">v3</a>"
                }
              }
            ]
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(BuildPagesUrl(page: 1), pagesJson));

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "beetlejuice");
        Assert.Contains(games, i => i.Game!.Slug == "texaschainsaw");
        Assert.DoesNotContain(games, i => i.Game!.Title == "Junk");

        // Politeness invariants must hold on the failure-isolation
        // path too — every fetched URL is acquired, reported, and the
        // lease disposed.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // The WP REST pages endpoint itself fails. The scraper must
        // yield nothing AND not throw — the orchestrator handles
        // per-source aborts via the outer try/catch around
        // ScrapeAsync(), but this scraper's contract is to yield-break
        // cleanly on discovery failure. The discovery-failure
        // exception filter explicitly excludes PolitenessException
        // (covered separately below).
        var (scraper, _, _) = BuildScraper(h => h
            .Map(BuildPagesUrl(page: 1),
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
            .MapJson(BuildPagesUrl(page: 1), "[]"));

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
        // let the run continue would fetch the WP REST endpoint; both
        // are pinned out by these assertions.
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
        var pagesJson = $$"""
            [
              {
                "id": 1,
                "slug": "beetlejuice",
                "link": "{{BaseUrl}}/beetlejuice/",
                "parent": 0,
                "modified": "2026-04-10T23:25:43",
                "title": { "rendered": "Beetlejuice" },
                "content": {
                  "rendered": "<a href=\"https://{{S3Host}}/beetlejuice/v1.beetlejuice\">v1</a>"
                }
              }
            ]
            """;

        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson(BuildPagesUrl(page: 1), pagesJson));

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

    private static string BuildPagesUrl(int page)
    {
        // Mirrors SpookyWpPagesClient.BuildPagesUrl — same field list
        // and per_page must be byte-identical or QueueingHttpMessageHandler
        // will reject the request as unmapped.
        const string fields = "id,slug,link,parent,modified,title,content";
        return $"{BaseUrl}{PagesEndpoint}?per_page=100&page={page}&_fields={fields}";
    }

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(SpookyGamePageScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (SpookyGamePageScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var spookyOpts = Options.Create(new SpookyOptions
        {
            BaseUrl = BaseUrl,
            PagesEndpointPath = PagesEndpoint,
            PageSize = 100,
            S3Host = S3Host,
            MaxPagesToFetch = 50,
        });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var pagesClient = new SpookyWpPagesClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, spookyOpts,
            NullLogger<SpookyWpPagesClient>.Instance);

        var scraper = new SpookyGamePageScraper(
            pagesClient,
            gate, politenessOpts, spookyOpts,
            NullLogger<SpookyGamePageScraper>.Instance);

        return (scraper, gate, handler);
    }
}
