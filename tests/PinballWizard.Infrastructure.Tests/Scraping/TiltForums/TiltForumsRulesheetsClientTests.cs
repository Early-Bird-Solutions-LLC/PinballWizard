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

    [Fact]
    public async Task DiscoverSubcategoryTopicUrlsAsync_NonNotFoundHttpError_StopsPaginationGracefully_ReturnsAccumulated()
    {
        // Page 0 succeeds and yields 2 URLs; page 1 returns a 500 (server error).
        // The method must not throw — it should stop pagination and return the 2
        // URLs already collected from page 0, losing no data already gathered.
        var (client, _, _) = BuildClient(h => h
            .MapHtml(SubcategoryPageUrl0, SubcategoryPage0Html)
            .Map(SubcategoryPageUrl(1), _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var urls = await client.DiscoverSubcategoryTopicUrlsAsync(CancellationToken.None);

        // Must not throw; must return the 2 URLs from page 0, not 0.
        Assert.Equal(2, urls.Count);
        Assert.Contains("https://tiltforums.com/t/rulesheet-master-list/7230", urls);
        Assert.Contains("https://tiltforums.com/t/godzilla-rulesheet/1", urls);
    }

    // Shape verified against the live "Transformers" topic page 2026-07-03:
    // #post_1 > div.post[itemprop='text'] holds h1/p/ul content; author and
    // timestamp live in #post_1's crawler-post-meta block.
    private const string TopicPageHtml = """
        <html><body>
        <div id='post_1' class='topic-body crawler-post'>
          <div class='crawler-post-meta'>
            <span class="creator" itemprop="author">
              <a itemprop="url" href='https://tiltforums.com/u/CaptainBZarre'><span itemprop='name'>CaptainBZarre</span></a>
            </span>
            <span class="crawler-post-infos">
              <time datetime='2026-05-21T15:03:35Z' class='post-time'>May 21, 2026, 3:03pm</time>
            </span>
          </div>
          <div class='post' itemprop='text'>
            <h1>Quick Links:</h1>
            <ul><li><a href="#heading--gameinfo">Game Information</a></li></ul>
            <h1 id="heading--gameinfo">Game Information &amp; Overview:</h1>
            <ul>
              <li>Lead Designer: Elliot Eismin</li>
              <li>Wiki Rulesheet based on Code Rev: 0.87
                <ul><li><em>Edit the Code revision, if applicable, when you make changes</em></li></ul>
              </li>
            </ul>
            <p><em>Transformers: More Than Meets the Eye</em> is Elliot Eismin's first machine as lead designer.</p>
            <h1 id="heading--modes">Main Modes:</h1>
            <p>Knock down the drop targets in front of Megatron, then shoot the scoop.</p>
          </div>
        </div>
        <div id='post_2' itemprop='comment' class='topic-body crawler-post'>
          <div class='post' itemprop='text'><p>Nice writeup!</p></div>
        </div>
        </body></html>
        """;

    private static TiltForumsRulesheetListing TransformersListing() => new()
    {
        GameTitle = "Transformers: More Than Meets The Eye",
        ManufacturerHeaderText = "Stern Pinball",
        TopicUrl = "https://tiltforums.com/t/transformers-more-than-meets-the-eye-rulesheet/10229",
    };

    [Fact]
    public async Task FetchRulesheetAsync_TransformersFixture_ParsesAllFields()
    {
        var (client, gate, _) = BuildClient(h => h.MapHtml(TransformersListing().TopicUrl, TopicPageHtml));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.NotNull(article);
        Assert.Equal("Transformers: More Than Meets The Eye", article!.GameTitle);
        Assert.Equal("CaptainBZarre", article.Author);
        Assert.Equal("0.87", article.CodeRevision);
        Assert.NotNull(article.PublishedAt);
        Assert.Equal(2026, article.PublishedAt!.Value.Year);
        Assert.Equal(5, article.PublishedAt!.Value.Month);

        // Body text includes wiki OP content, heading-prefixed.
        Assert.Contains("Megatron", article.BodyText, StringComparison.Ordinal);
        Assert.Contains("## Quick Links:", article.BodyText, StringComparison.Ordinal);
        Assert.Contains("## Main Modes:", article.BodyText, StringComparison.Ordinal);

        // Reply post ("Nice writeup!") must NOT leak into the wiki OP body.
        Assert.DoesNotContain("Nice writeup", article.BodyText, StringComparison.Ordinal);

        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task FetchRulesheetAsync_HttpFailure_ReturnsNull()
    {
        var (client, gate, _) = BuildClient(h => h
            .Map(TransformersListing().TopicUrl, _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.Null(article);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task FetchRulesheetAsync_NoPostOneContent_ReturnsNull()
    {
        const string html = "<html><body><p>Not a Discourse topic page.</p></body></html>";
        var (client, _, _) = BuildClient(h => h.MapHtml(TransformersListing().TopicUrl, html));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.Null(article);
    }

    [Fact]
    public async Task FetchRulesheetAsync_NoCodeRevMarker_CodeRevisionIsNull()
    {
        const string html = """
            <html><body>
            <div id='post_1' class='topic-body crawler-post'>
              <div class='crawler-post-meta'>
                <span class="creator" itemprop="author"><span itemprop='name'>SomeUser</span></span>
              </div>
              <div class='post' itemprop='text'>
                <h1>Overview</h1>
                <p>No code revision marker in this fixture.</p>
              </div>
            </div>
            </body></html>
            """;

        var (client, _, _) = BuildClient(h => h.MapHtml(TransformersListing().TopicUrl, html));

        var article = await client.FetchRulesheetAsync(TransformersListing(), CancellationToken.None);

        Assert.NotNull(article);
        Assert.Null(article!.CodeRevision);
        Assert.Null(article.PublishedAt);
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
