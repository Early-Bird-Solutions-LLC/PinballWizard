using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Spooky;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Spooky;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="SpookySupportPageScraper"/>.
/// Exercises the full <see cref="ISourceScraper.ScrapeAsync"/> flow (WP REST
/// child-page discovery → per-page PDF extraction → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test surface
/// cannot reach: provenance-field propagation, per-page failure isolation,
/// firmware-only page skipping, and polite-scraping invariants.
/// </summary>
/// <remarks>
/// Fixtures derived from the 2026-06-25 recon of Spooky's Game Support hub.
/// The WP REST endpoint used is:
///   /wp-json/wp/v2/pages?parent=476&amp;per_page=100&amp;_fields=id,slug,link,parent,modified,title,content
/// </remarks>
public sealed class SpookySupportPageScraperTests
{
    private const string BaseUrl = "https://www.spookypinball.com";
    private const string PagesEndpoint = "/wp-json/wp/v2/pages";
    private const int GameSupportParentId = 476;

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ScrapeAsync_PageWithPdfs_YieldsLinksWithFullProvenance()
    {
        // hwn-um-manual page: three PDFs, verified 2026-06-25.
        var supportPagesJson = $$"""
            [
              {
                "id": 1456,
                "slug": "hwn-um-manual",
                "link": "{{BaseUrl}}/game-support/hwn-um-manual/",
                "parent": {{GameSupportParentId}},
                "modified": "2023-09-15T12:00:00",
                "title": { "rendered": "HWN/UM Manual" },
                "content": {
                  "rendered": "<a href=\"/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf\">Switch Positions</a><a href=\"/wp-content/uploads/2023/09/Coil-Chart2.pdf\">Coil Chart</a><a href=\"/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf\">Board Layout</a>"
                }
              }
            ]
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), supportPagesJson)
            .MapHtml($"{BaseUrl}/game-support/", "<html><body>no hub PDFs</body></html>"));

        var items = await ScrapeAllAsync(scraper);

        // Three PDFs from one page → three ScrapedItems, all .Link (no .Game).
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Link));
        Assert.All(items, i => Assert.Null(i.Game));

        // Verify full provenance on every item.
        Assert.All(items, i =>
        {
            Assert.Equal(SourceType.SpookyPinballSupportPage, i.SourceType);
            Assert.Equal("Spooky Pinball Support Page", i.DiscoveryContext);
            Assert.Equal($"{BaseUrl}/game-support/hwn-um-manual/", i.DiscoveryUrl);
        });

        // Verify the PDF URLs are absolutized.
        Assert.Contains(items, i => i.Link!.FileUrl
            == "https://www.spookypinball.com/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf");
        Assert.Contains(items, i => i.Link!.FileUrl
            == "https://www.spookypinball.com/wp-content/uploads/2023/09/Coil-Chart2.pdf");
        Assert.Contains(items, i => i.Link!.FileUrl
            == "https://www.spookypinball.com/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf");

        // hwn-um-* slug maps to "halloween" canonical game slug.
        Assert.All(items, i => Assert.Equal("halloween", i.Link!.GameSlug));

        // Link text is captured for document-type classification.
        Assert.Contains(items, i => i.Link!.LinkText == "Switch Positions");
        Assert.Contains(items, i => i.Link!.LinkText == "Coil Chart");
        Assert.Contains(items, i => i.Link!.LinkText == "Board Layout");

        // Politeness invariants: every fetched URL passed through the gate.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.All(gate.Reported, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Status));
    }

    // ── Hub-page direct discovery (Path 2) ───────────────────────────────────

    [Fact]
    public async Task ScrapeAsync_HubPageWithPdfs_YieldsLinksWithHubPageProvenance()
    {
        // No WP child pages — only the hub page itself carries PDFs, matching
        // Spooky's 2026-07 pattern for newer titles (Beetlejuice, Rick and
        // Morty, etc. — see SpookySupportPageScraper's Path 2 doc comment).
        var hubHtml = """
            <html><body>
            <a href="/wp-content/uploads/2026/07/Beetlejuice-Manual.pdf">Owner's Manual</a>
            </body></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), "[]")
            .MapHtml($"{BaseUrl}/game-support/", hubHtml));

        var items = await ScrapeAllAsync(scraper);

        Assert.Single(items);
        var item = items[0];
        Assert.NotNull(item.Link);
        Assert.Null(item.Game);
        Assert.Equal(SourceType.SpookyPinballSupportPage, item.SourceType);
        Assert.Equal("Spooky Pinball Game Support Hub", item.DiscoveryContext);
        Assert.Equal($"{BaseUrl}/game-support/", item.DiscoveryUrl);
        Assert.Equal(
            "https://www.spookypinball.com/wp-content/uploads/2026/07/Beetlejuice-Manual.pdf",
            item.Link!.FileUrl);

        // Game slug is derived from the filename, per
        // SpookySupportPageExtractor.DeriveSlugFromFilename: strips "-Manual".
        Assert.Equal("beetlejuice", item.Link!.GameSlug);
        Assert.Equal("Owner's Manual", item.Link!.LinkText);

        // Politeness invariants: WP-REST discovery (empty) + hub-page fetch,
        // both succeed, so every request is reported.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_HubPageFetchFails_NonFatal_ChildPagePdfsStillYielded()
    {
        // The hub-page fetch (Path 2) is deliberately left unmapped in this
        // fixture, so QueueingHttpMessageHandler throws UnexpectedRequestException
        // when the scraper attempts it. Per SpookySupportPageScraper.ScrapeAsync's
        // Path 2 catch block, this is non-fatal: the run continues and the
        // Path 1 (WP child-page) results still yield in full.
        var supportPagesJson = $$"""
            [
              {
                "id": 1456,
                "slug": "hwn-um-manual",
                "link": "{{BaseUrl}}/game-support/hwn-um-manual/",
                "parent": {{GameSupportParentId}},
                "modified": "2023-09-15T12:00:00",
                "title": { "rendered": "HWN/UM Manual" },
                "content": {
                  "rendered": "<a href=\"/wp-content/uploads/2023/09/rules.pdf\">Rules</a>"
                }
              }
            ]
            """;

        // Deliberately no .MapHtml for the hub page — simulates a hub-page
        // fetch failure (network error, 5xx, etc. all surface the same way
        // through PoliteScraperBase: an exception out of GetStringPolitelyAsync).
        var (scraper, _, _) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), supportPagesJson));

        var items = await ScrapeAllAsync(scraper);

        // Path 1's single PDF still yields despite Path 2 failing entirely.
        Assert.Single(items);
        Assert.Equal($"{BaseUrl}/game-support/hwn-um-manual/", items[0].DiscoveryUrl);
        Assert.Equal("Spooky Pinball Support Page", items[0].DiscoveryContext);
    }

    [Fact]
    public async Task ScrapeAsync_MixedPages_YieldsPdfPagesAndSkipsFirmwareOnlyPages()
    {
        // Mix: hwn-um-manual (has PDFs) + halloween firmware page (no PDFs).
        // The firmware-only page must be silently skipped — no items, no error.
        var supportPagesJson = $$"""
            [
              {
                "id": 1456,
                "slug": "hwn-um-manual",
                "link": "{{BaseUrl}}/game-support/hwn-um-manual/",
                "parent": {{GameSupportParentId}},
                "modified": "2023-09-15T12:00:00",
                "title": { "rendered": "HWN/UM Manual" },
                "content": {
                  "rendered": "<a href=\"/wp-content/uploads/2023/09/rules.pdf\">Rules</a>"
                }
              },
              {
                "id": 1450,
                "slug": "halloween",
                "link": "{{BaseUrl}}/game-support/halloween/",
                "parent": {{GameSupportParentId}},
                "modified": "2026-06-11T10:00:00",
                "title": { "rendered": "Halloween" },
                "content": {
                  "rendered": "<a href=\"https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.18.1/code_H78.pkg\">v1.18.1</a>"
                }
              }
            ]
            """;

        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), supportPagesJson)
            .MapHtml($"{BaseUrl}/game-support/", "<html><body></body></html>"));

        var items = await ScrapeAllAsync(scraper);

        // Only the PDF page yields items; the firmware-only page is silently skipped.
        Assert.Single(items);
        Assert.Equal($"{BaseUrl}/game-support/hwn-um-manual/", items[0].DiscoveryUrl);
        Assert.Equal("halloween", items[0].Link!.GameSlug);  // hwn-um-* → halloween

        // Politeness invariants still hold even when pages are skipped.
        Assert.Equal(gate.Acquired.Count, gate.LeasesDisposed);
    }

    [Fact]
    public async Task ScrapeAsync_NoSupportPages_YieldsNothing()
    {
        // WP REST returns empty array — no child pages under the Game Support hub.
        // This is the graceful-empty case; no error, no items.
        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), "[]")
            .MapHtml($"{BaseUrl}/game-support/", "<html><body></body></html>"));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
        Assert.Equal(gate.Acquired.Count, gate.LeasesDisposed);
    }

    // ── Failure isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // WP REST endpoint itself returns 500. The scraper must yield nothing
        // and NOT throw — per-source abort is handled by the orchestrator's
        // outer try/catch.
        var (scraper, _, _) = BuildScraper(h => h
            .Map(BuildSupportPagesUrl(),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the orchestrator needs to
        // see it to mark the source aborted. The discovery-failure filter
        // explicitly excludes PolitenessException.
        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), "[]"));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });
    }

    // ── Document-type classification contract ────────────────────────────────

    [Fact]
    public async Task ScrapeAsync_RulesLinkText_ClassifiedAsRulesheetByOrchestrator()
    {
        // Classification is performed by ScraperOrchestrator.ClassifyDocumentType,
        // not by the scraper itself. We verify the scraper emits the correct
        // link text ("Rules" / "Rule Sheet") so the orchestrator can classify it.
        // This test pins the link-text fidelity chain from page → ScrapedItem.
        var supportPagesJson = $$"""
            [
              {
                "id": 9999,
                "slug": "beetlejuice-docs",
                "link": "{{BaseUrl}}/game-support/beetlejuice-docs/",
                "parent": {{GameSupportParentId}},
                "modified": "2024-01-01T00:00:00",
                "title": { "rendered": "Beetlejuice Docs" },
                "content": {
                  "rendered": "<a href=\"/wp-content/uploads/2024/01/Beetlejuice-Rules.pdf\">Beetlejuice Rules</a><a href=\"/wp-content/uploads/2024/01/Beetlejuice-Manual.pdf\">Owner&#39;s Manual</a>"
                }
              }
            ]
            """;

        var (scraper, _, _) = BuildScraper(h => h
            .MapJson(BuildSupportPagesUrl(), supportPagesJson)
            .MapHtml($"{BaseUrl}/game-support/", "<html><body></body></html>"));

        var items = await ScrapeAllAsync(scraper);

        Assert.Equal(2, items.Count);

        // Rules PDF: link text "Beetlejuice Rules" → ClassifyDocumentType yields Rulesheet.
        var rulesItem = items.Single(i => i.Link!.FileUrl.Contains("Rules"));
        Assert.Equal("Beetlejuice Rules", rulesItem.Link!.LinkText);

        // Manual PDF: link text "Owner's Manual" → ClassifyDocumentType yields Manual.
        var manualItem = items.Single(i => i.Link!.FileUrl.Contains("Manual"));
        Assert.Equal("Owner's Manual", manualItem.Link!.LinkText);

        // Verify classification via orchestrator helper (unit-tests the classification
        // logic without needing a full orchestrator).
        var rulesType = PinballWizard.Application.ScraperOrchestrator.ClassifyDocumentType(
            rulesItem.Link!, rulesItem.DiscoveryContext, rulesItem.SourceType);
        var manualType = PinballWizard.Application.ScraperOrchestrator.ClassifyDocumentType(
            manualItem.Link!, manualItem.DiscoveryContext, manualItem.SourceType);

        Assert.Equal(DocumentType.Rulesheet, rulesType);
        Assert.Equal(DocumentType.Manual, manualType);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSupportPagesUrl()
    {
        // Must match SpookySupportPageScraper.BuildSupportPagesUrl byte-for-byte
        // so QueueingHttpMessageHandler resolves the mapped response.
        const string fields = "id,slug,link,parent,modified,title,content";
        return $"{BaseUrl}{PagesEndpoint}?parent={GameSupportParentId}&per_page=100&_fields={fields}";
    }

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(SpookySupportPageScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (SpookySupportPageScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var spookyOpts = Options.Create(new SpookyOptions
        {
            BaseUrl = BaseUrl,
            PagesEndpointPath = PagesEndpoint,
            PageSize = 100,
            S3Host = "spookypinball.s3.us-east-2.amazonaws.com",
            MaxPagesToFetch = 50,
            GameSupportParentPageId = GameSupportParentId,
        });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var scraper = new SpookySupportPageScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, spookyOpts,
            NullLogger<SpookySupportPageScraper>.Instance);

        return (scraper, gate, handler);
    }
}
