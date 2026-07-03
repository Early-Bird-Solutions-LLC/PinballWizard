using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsRulesheetsClientTests
{
    private const string BaseUrl = "https://tiltforums.com";
    private const string MasterListUrl = $"{BaseUrl}/t/rulesheet-master-list/7230";

    // Shape verified against the live page 2026-07-03: manufacturer h2[id]
    // headings, each followed by a sibling div.md-table > table > tbody > tr,
    // all inside #post_1. The trailing "Legacy..." heading has no id and must
    // be excluded.
    private const string MasterListHtml = """
        <html><body>
        <div id='post_1' class='topic-body crawler-post'>
          <div class='post' itemprop='text'>
            <h2 id="heading--stern">Stern Pinball:</h2>
            <div class="md-table">
            <table>
            <thead><tr><th>Game</th><th>Released</th></tr></thead>
            <tbody>
            <tr><td><a href="https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229">Transformers: More Than Meets The Eye</a></td><td>June 2026</td></tr>
            <tr><td><a href="https://tiltforums.com/t/star-wars-stern-rulesheet/2812">Star Wars</a></td><td>2017</td></tr>
            </tbody>
            </table>
            </div>
            <h2 id="heading--spooky">Spooky Pinball:</h2>
            <div class="md-table">
            <table>
            <tbody>
            <tr><td><a href="https://tiltforums.com/t/star-wars-fall-of-the-empire-rulesheet/9872">Star Wars: Fall of the Empire</a></td><td>2025</td></tr>
            </tbody>
            </table>
            </div>
            <h2>Legacy non-wiki Rulesheet List (external)</h2>
            <p><a href="https://replayfoundation.org/papa/">PAPA Rulesheets</a></p>
          </div>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task DiscoverRulesheetsAsync_TwoManufacturerSections_ReturnsAllListings()
    {
        var (client, gate, _) = BuildClient(h => h.MapHtml(MasterListUrl, MasterListHtml));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.Equal(3, listings.Count);

        Assert.Equal("Transformers: More Than Meets The Eye", listings[0].GameTitle);
        Assert.Equal("Stern Pinball", listings[0].ManufacturerHeaderText);
        Assert.Equal("https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229", listings[0].TopicUrl);

        Assert.Equal("Star Wars", listings[1].GameTitle);
        Assert.Equal("Stern Pinball", listings[1].ManufacturerHeaderText);

        Assert.Equal("Star Wars: Fall of the Empire", listings[2].GameTitle);
        Assert.Equal("Spooky Pinball", listings[2].ManufacturerHeaderText);

        // Politeness: exactly one fetch went through the gate.
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverRulesheetsAsync_LegacyHeadingWithNoId_Excluded()
    {
        var (client, _, _) = BuildClient(h => h.MapHtml(MasterListUrl, MasterListHtml));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.DoesNotContain(listings, l => l.TopicUrl.Contains("replayfoundation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(listings, l => l.ManufacturerHeaderText.Contains("Legacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverRulesheetsAsync_HttpFailure_ReturnsEmpty_NoFabrication()
    {
        var (client, _, _) = BuildClient(h => h.Map(MasterListUrl, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.Empty(listings);
    }

    [Fact]
    public async Task DiscoverRulesheetsAsync_SectionWithNoTable_SkippedWithoutThrowing()
    {
        const string html = """
            <html><body>
            <div id='post_1' class='topic-body crawler-post'>
              <div class='post' itemprop='text'>
                <h2 id="heading--empty">Empty Manufacturer:</h2>
                <p>No table follows this heading.</p>
                <h2 id="heading--stern">Stern Pinball:</h2>
                <div class="md-table"><table><tbody>
                <tr><td><a href="https://tiltforums.com/t/godzilla-rulesheet/1">Godzilla</a></td></tr>
                </tbody></table></div>
              </div>
            </div>
            </body></html>
            """;

        var (client, _, _) = BuildClient(h => h.MapHtml(MasterListUrl, html));

        var listings = await client.DiscoverRulesheetsAsync(CancellationToken.None);

        Assert.Single(listings);
        Assert.Equal("Godzilla", listings[0].GameTitle);
    }

    private const string SubcategoryPageUrl0 = $"{BaseUrl}/c/game-specific/rulesheet-wikis/18";
    private static string SubcategoryPageUrl(int page) => $"{SubcategoryPageUrl0}?page={page}";

    // Shape verified against the live page 2026-07-03: table.topic-list >
    // tr.topic-list-item, each with a.title.raw-link.raw-topic-link[href].
    private const string SubcategoryPage0Html = """
        <html><body>
        <table class='topic-list'>
        <tbody>
        <tr class="topic-list-item">
          <td class="main-link">
            <a itemprop='url' href='https://tiltforums.com/t/rulesheet-master-list/7230' class='title raw-link raw-topic-link'>Rulesheet Master List</a>
          </td>
        </tr>
        <tr class="topic-list-item">
          <td class="main-link">
            <a itemprop='url' href='https://tiltforums.com/t/godzilla-rulesheet/1' class='title raw-link raw-topic-link'>Godzilla Rulesheet</a>
          </td>
        </tr>
        </tbody>
        </table>
        </body></html>
        """;

    private const string SubcategoryPage1Html = """
        <html><body>
        <table class='topic-list'>
        <tbody>
        <tr class="topic-list-item">
          <td class="main-link">
            <a itemprop='url' href='https://tiltforums.com/t/jaws-rulesheet/2' class='title raw-link raw-topic-link'>Jaws Rulesheet</a>
          </td>
        </tr>
        </tbody>
        </table>
        </body></html>
        """;

    [Fact]
    public async Task DiscoverSubcategoryTopicUrlsAsync_TwoPages_ReturnsAllUrls()
    {
        var (client, gate, _) = BuildClient(h => h
            .MapHtml(SubcategoryPageUrl0, SubcategoryPage0Html)
            .MapHtml(SubcategoryPageUrl(1), SubcategoryPage1Html)
            .Map(SubcategoryPageUrl(2), _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var urls = await client.DiscoverSubcategoryTopicUrlsAsync(CancellationToken.None);

        Assert.Equal(3, urls.Count);
        Assert.Contains("https://tiltforums.com/t/rulesheet-master-list/7230", urls);
        Assert.Contains("https://tiltforums.com/t/godzilla-rulesheet/1", urls);
        Assert.Contains("https://tiltforums.com/t/jaws-rulesheet/2", urls);
        Assert.Equal(gate.Acquired.Count, gate.Reported.Count);
    }

    [Fact]
    public async Task DiscoverSubcategoryTopicUrlsAsync_EmptyPage_StopsPagination()
    {
        const string emptyPage = "<html><body><table class='topic-list'><tbody></tbody></table></body></html>";

        var (client, _, _) = BuildClient(h => h
            .MapHtml(SubcategoryPageUrl0, SubcategoryPage0Html)
            .MapHtml(SubcategoryPageUrl(1), emptyPage));

        var urls = await client.DiscoverSubcategoryTopicUrlsAsync(CancellationToken.None);

        Assert.Equal(2, urls.Count);
    }

    private static (TiltForumsRulesheetsClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(BaseUrl),
        };

        var client = new TiltForumsRulesheetsClient(
            httpClient,
            gate,
            Options.Create(new PolitenessOptions()),
            NullLogger<TiltForumsRulesheetsClient>.Instance);

        return (client, gate, handler);
    }
}
