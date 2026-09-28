using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.ChicagoGaming;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="CgcGamePageScraper"/>.
/// Exercises the full <see cref="Core.Scraping.ISourceScraper.ScrapeAsync"/>
/// flow (menu fetch → per-page fetch → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test
/// surface cannot reach: yield order, provenance-field propagation
/// onto <see cref="ScrapedItem"/>, per-page failure isolation, and
/// the polite-scraping invariants (every fetched URL passes through
/// the gate, every response is reported back).
/// </summary>
/// <remarks>
/// This is the proof-of-concept for the family-wide test-infra
/// pattern. CGC was picked because it exercises BOTH yield kinds
/// (<c>.Game</c> and <c>.Link</c>); other scrapers in the family
/// can be backfilled with the same approach.
/// </remarks>
public sealed class CgcGamePageScraperTests
{
    private const string BaseUrl = CgcCapturedFixtures.BaseUrl;

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGameThenLinksWithProvenance()
    {
        const string indexHtml = """
            <html><body>
              <a href="/coinop/medieval-madness">MM</a>
              <a href="/coinop/pulp-fiction">Pulp</a>
            </body></html>
            """;
        const string mmHtml = """
            <html>
              <head><title>Medieval Madness Merlin Edition Pinball | Chicago Gaming Company</title></head>
              <body>
                <h1>MM</h1>
                <a href="/manuals/MMR_Manual.pdf">Manual</a>
              </body>
            </html>
            """;
        const string pulpHtml = """
            <html>
              <head><title>Pulp Fiction Pinball | Chicago Gaming Company</title></head>
              <body>
                <a href="/brochures/Pulp_Fiction_Brochure.pdf">Brochure</a>
                <a href="/warranties/Pulp_Fiction_Warranty.pdf">Warranty</a>
              </body>
            </html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml($"{BaseUrl}/", indexHtml)
            .MapHtml($"{BaseUrl}/coinop/medieval-madness", mmHtml)
            .MapHtml($"{BaseUrl}/coinop/pulp-fiction", pulpHtml));

        var items = await ScrapeAllAsync(scraper);

        // Yield order per machine: Game, then Link(s); machines in index order.
        Assert.Equal(5, items.Count);
        Assert.NotNull(items[0].Game);
        Assert.Equal("Medieval Madness Merlin Edition Pinball", items[0].Game!.Title);
        Assert.Equal("game_cgc_medieval-madness", items[0].Game!.GameId);

        Assert.NotNull(items[1].Link);
        Assert.EndsWith("MMR_Manual.pdf", items[1].Link!.FileUrl);
        Assert.Equal("medieval-madness", items[1].Link!.GameSlug);

        Assert.NotNull(items[2].Game);
        Assert.Equal("Pulp Fiction Pinball", items[2].Game!.Title);

        Assert.NotNull(items[3].Link);
        Assert.NotNull(items[4].Link);

        // Provenance propagation: every yielded item carries the discovery URL,
        // the discovery context, and the source-type sentinel.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.ChicagoGamingGamePage, item.SourceType);
            Assert.Equal("Chicago Gaming Game Page", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.Contains("/coinop/", item.DiscoveryUrl);
        }

        // Politeness invariants: every fetched URL passed through the gate
        // (acquire + report), every lease was disposed, AND the URL the gate
        // saw is byte-identical to the URL the wire saw — so a future
        // refactor that re-canonicalises between gate and send cannot
        // silently throttle a different origin.
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

        // Provenance: the GameRecord carries the page URL and discovery
        // sentinel forward — these are what survive into catalog.json
        // and into Phase 2 RAG citations. Provenance is the project's
        // load-bearing principle; pin it in the template.
        var firstGame = items[0].Game!;
        Assert.Equal($"{BaseUrl}/coinop/medieval-madness", firstGame.Source!.ScrapedFrom);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("cgc_coinop", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_CapturedSite_DiscoversEveryMachineFromTheSiteRoot()
    {
        var (scraper, gate, handler) = BuildScraper(h =>
        {
            h.MapHtml($"{BaseUrl}/", CgcCapturedFixtures.Home());
            foreach (var slug in CgcCapturedFixtures.MachineSlugs)
            {
                h.MapHtml($"{BaseUrl}/coinop/{slug}", CgcCapturedFixtures.MachinePage(slug));
            }
        });

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).Select(i => i.Game!).ToList();
        Assert.Equal(
            [
                ("attack-from-mars", "Attack From Mars! Remake"),
                ("cactus-canyon", "Cactus Canyon Remake"),
                ("medieval-madness", "Medieval Madness Merlin Edition Pinball"),
                ("monster-bash", "Monster Bash Remake"),
                ("pulp-fiction", "Pulp Fiction Pinball"),
            ],
            games.Select(g => (g.Slug, g.Title)).OrderBy(g => g.Slug, StringComparer.Ordinal).ToArray());
        Assert.All(games, g => Assert.Equal($"{BaseUrl}/coinop/{g.Slug}", g.Source!.ScrapedFrom));

        var linksBySlug = items
            .Where(i => i.Link is not null)
            .GroupBy(i => i.Link!.GameSlug!)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Link!.FileUrl).ToList());
        Assert.Equal(11, linksBySlug.Values.Sum(l => l.Count));
        Assert.False(linksBySlug.ContainsKey("cactus-canyon"));
        Assert.Contains($"{BaseUrl}/manuals/MMR_Manual_Rev_1_2.pdf", linksBySlug["medieval-madness"]);
        Assert.Contains($"{BaseUrl}/manuals/MB_Manual_Rev_1.pdf", linksBySlug["monster-bash"]);
        Assert.Contains($"{BaseUrl}/brochures/AFM_Brochure.pdf", linksBySlug["attack-from-mars"]);
        Assert.Contains($"{BaseUrl}/brochures/Pulp_Fiction_Pinball_Rules_Manual.pdf", linksBySlug["pulp-fiction"]);
        Assert.Equal(5, linksBySlug["pulp-fiction"].Count);

        // Each item's discovery URL is the machine page it came from, not the site root.
        foreach (var item in items)
        {
            var slug = item.Game?.Slug ?? item.Link!.GameSlug;
            Assert.Equal($"{BaseUrl}/coinop/{slug}", item.DiscoveryUrl);
            Assert.Equal("Chicago Gaming Game Page", item.DiscoveryContext);
            Assert.Equal(SourceType.ChicagoGamingGamePage, item.SourceType);
        }

        // One root fetch plus one per machine; the upgrade sub-page is never requested.
        Assert.Equal(1 + CgcCapturedFixtures.MachineSlugs.Length, handler.Requests.Count);
        Assert.Equal($"{BaseUrl}/", handler.Requests[0].AbsoluteUri);
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath.EndsWith("/upgrade", StringComparison.Ordinal));
        Assert.Equal(handler.Requests.Select(u => u.AbsoluteUri), gate.Acquired.Select(u => u.AbsoluteUri));
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // One bad page in the middle should NOT prevent siblings from yielding.
        // The scraper logs a warning, returns null/empty for that page, and
        // continues to the next.
        const string indexHtml = """
            <html><body>
              <a href="/coinop/medieval-madness">MM</a>
              <a href="/coinop/broken">Broken</a>
              <a href="/coinop/pulp-fiction">Pulp</a>
            </body></html>
            """;
        const string mmHtml = """<html><head><title>MM | Chicago Gaming Company</title></head></html>""";
        const string pulpHtml = """<html><head><title>Pulp | Chicago Gaming Company</title></head></html>""";

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml($"{BaseUrl}/", indexHtml)
            .MapHtml($"{BaseUrl}/coinop/medieval-madness", mmHtml)
            .Map($"{BaseUrl}/coinop/broken",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{BaseUrl}/coinop/pulp-fiction", pulpHtml));

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "medieval-madness");
        Assert.Contains(games, i => i.Game!.Slug == "pulp-fiction");
        Assert.DoesNotContain(games, i => i.Game!.Slug == "broken");

        // Politeness invariants must hold on the failure path too — the
        // 500 response must still be reported back so the 429-streak
        // detector can see real failures, and every acquire still has
        // a matching report and lease-dispose.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.Contains(
            gate.Reported,
            r => r.Status == System.Net.HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // The index page itself fails, as /coinop/ did with a 404 in #967.
        // The scraper must yield nothing AND not throw: it logs the cause
        // and yield-breaks, and the orchestrator's yield guard (#857) then
        // fails the run for this source.
        var (scraper, _, _) = BuildScraper(h => h
            .Map($"{BaseUrl}/",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_IndexLinksNoMachines_YieldsNothingAndFetchesNoMachinePage()
    {
        // The root loads but its menu no longer links any machine. The
        // client throws, the scraper yield-breaks, and the orchestrator's
        // yield guard fails the run instead of recording a quiet success.
        const string indexHtml = """
            <html><body>
              <a href="/arcade/foosball">Foosball</a>
              <a href="/coinop/cactus-canyon/upgrade">Upgrade</a>
            </body></html>
            """;
        var (scraper, _, handler) = BuildScraper(h => h.MapHtml($"{BaseUrl}/", indexHtml));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
        Assert.Equal([$"{BaseUrl}/"], handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task ScrapeAsync_CallerCancelsDuringPageFetch_Throws()
    {
        using var cts = new CancellationTokenSource();
        const string indexHtml = """
            <html><body>
              <a href="/coinop/medieval-madness">MM</a>
              <a href="/coinop/pulp-fiction">Pulp</a>
            </body></html>
            """;
        var (scraper, _, handler) = BuildScraper(h => h
            .MapHtml($"{BaseUrl}/", indexHtml)
            .Map($"{BaseUrl}/coinop/medieval-madness", _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            })
            .MapHtml($"{BaseUrl}/coinop/pulp-fiction", "<html/>"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(cts.Token))
            {
            }
        });
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath == "/coinop/pulp-fiction");
    }

    [Fact]
    public async Task ScrapeAsync_PageFetchTimesOut_SkipsPageAndContinues()
    {
        // HttpClient reports its own timeout as TaskCanceledException while
        // the caller's token is still live. That is one bad page, not a
        // cancelled run.
        const string indexHtml = """
            <html><body>
              <a href="/coinop/medieval-madness">MM</a>
              <a href="/coinop/pulp-fiction">Pulp</a>
            </body></html>
            """;
        const string pulpHtml = """<html><head><title>Pulp Fiction Pinball | Chicago Gaming Company</title></head></html>""";
        var (scraper, _, _) = BuildScraper(h => h
            .MapHtml($"{BaseUrl}/", indexHtml)
            .Map($"{BaseUrl}/coinop/medieval-madness", _ => throw new TaskCanceledException("timeout"))
            .MapHtml($"{BaseUrl}/coinop/pulp-fiction", pulpHtml));

        var items = await ScrapeAllAsync(scraper);

        var game = Assert.Single(items);
        Assert.Equal("pulp-fiction", game.Game!.Slug);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the orchestrator
        // needs to see it so the source is marked aborted for the run
        // (and the next scraper still gets to run via the orchestrator's
        // outer try/catch). The scraper-level discovery-failure filter
        // explicitly excludes PolitenessException; the per-page
        // TryExtractAsync also rethrows it explicitly.
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml($"{BaseUrl}/", """<html/>""")
            .MapHtml($"{BaseUrl}/coinop/medieval-madness", "<html/>"));

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
        // so the wire must show zero requests and the gate must show zero
        // reports. A regression that swallowed the exception and let the
        // run continue would fetch the site root and possibly more pages; both
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
        const string indexHtml = """<html><body><a href="/coinop/medieval-madness">MM</a></body></html>""";
        var (scraper, gate, _) = BuildScraper(h => h
            .MapHtml($"{BaseUrl}/", indexHtml)
            .MapHtml($"{BaseUrl}/coinop/medieval-madness", "<html/>"));

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

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(CgcGamePageScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (CgcGamePageScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var options = Options.Create(new ChicagoGamingOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        // Two HttpClients share the handler — one feeds the menu client,
        // the other feeds the scraper itself. Production wires them
        // separately via typed-client DI; the test mirrors that.
        var menuClient = new CgcMenuClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, options,
            NullLogger<CgcMenuClient>.Instance);

        var scraper = new CgcGamePageScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            menuClient,
            gate, politenessOpts,
            NullLogger<CgcGamePageScraper>.Instance);

        return (scraper, gate, handler);
    }
}
