using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers.Freshdesk;

public sealed class FreshdeskSolutionsClientTests
{
    private const string BaseUrl = "https://pinballbrothers.freshdesk.com";
    private const string SolutionsUrl = $"{BaseUrl}/support/solutions";

    // Trimmed to the two load-bearing categories (General, FAQs QUEEN) —
    // real markup shape captured 2026-07-03.
    private const string SolutionsHomeHtml = """
        <html><body>
        <section id="solutions-home">
          <div class="cs-s">
            <h3 class="heading"> <a href="/support/solutions/80000165341">General</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list">
                <div class="list-lead">
                  <a href="/support/solutions/folders/80000680701" title="Service Bulletin"> Service Bulletin <span class='item-count'>4</span></a>
                </div>
              </section>
            </div>
          </div>
          <div class="cs-s">
            <h3 class="heading"> <a href="/support/solutions/80000460814">FAQs QUEEN</a> </h3>
            <div class="cs-g-c">
              <section class="cs-g article-list">
                <div class="list-lead">
                  <a href="/support/solutions/folders/80000701915" title="Queen - General"> Queen - General <span class='item-count'>4</span></a>
                </div>
              </section>
              <section class="cs-g article-list">
                <div class="list-lead">
                  <a href="/support/solutions/folders/80000722110" title="QUEEN - Electronics"> QUEEN - Electronics <span class='item-count'>1</span></a>
                </div>
              </section>
            </div>
          </div>
        </section>
        </body></html>
        """;

    // Real markup from folder 80000432961 page 1 (10 of 14 articles), with a
    // "next" pagination link to page 2.
    private const string FolderPage1Html = """
        <html><body>
        <h2 class="heading">ALIEN - General</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80000843825--how-to-rebuild-machine-alien-4-0" class="c-link">"How-to" rebuild machine - Alien 4.0</a></div></div>
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80000707814-alien-game-rules" class="c-link">Alien Game rules</a></div></div>
        </section>
        <div class="pagination"><ul> <li class="prev disabled"><a>&laquo; Previous</a></li> <li class="active"><a>1</a></li> <li><a href="/support/solutions/folders/80000432961/page/2">2</a></li> <li class="next"><a href="/support/solutions/folders/80000432961/page/2">Next &raquo;</a></li> </ul></div>
        </body></html>
        """;

    // Real markup from folder 80000432961 page 2 (remaining 4 articles) — "next" is disabled.
    private const string FolderPage2Html = """
        <html><body>
        <h2 class="heading">ALIEN - General</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80001068353-alien-rulesheet-explanation" class="c-link">ALIEN - Rulesheet explanation</a></div></div>
        </section>
        <div class="pagination"><ul> <li class="prev"><a href="/support/solutions/folders/80000432961/page/1">&laquo; Previous</a></li> <li><a href="/support/solutions/folders/80000432961/page/1">1</a></li> <li class="active"><a>2</a></li> <li class="next disabled"><a>Next &raquo;</a></li> </ul></div>
        </body></html>
        """;

    // Real markup from folder 80000722109 (ALIEN - Electronics, 1 article) — no pagination div at all.
    private const string SinglePageFolderHtml = """
        <html><body>
        <h2 class="heading">ALIEN - Electronics</h2>
        <section class="article-list c-list">
            <div class="c-row c-article-row"><div class="ellipsis article-title"> <a href="/support/solutions/articles/80001155211-alien-schematics" class="c-link">Alien - Schematics</a></div></div>
        </section>
        </body></html>
        """;

    private const string ArticleHtml = """
        <html><body>
        <h2 class="heading">QUEEN Pinball - Technical Manual <a href="#" id="print-article"><span>Print</span></a></h2>
        <article class="article-body" id="article-body"><p>Here in attach you can find the QUEEN Technical Manual</p></article>
        <div class="cs-g-c attachments"><div class="attachment"><div class="attach_content"><div class="ellipsis">
            <a href="/helpdesk/attachments/80209470065" class="filename" title='QUEEN PINBALL TECHNICAL GAME MANUAL R1.pdf'>QUEEN PINBAL...</a>
        </div></div></div></div>
        </body></html>
        """;

    [Fact]
    public async Task DiscoverFoldersAsync_ParsesCategoryAndFolderNames()
    {
        var (client, gate, handler) = BuildClient(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));

        var folders = await client.DiscoverFoldersAsync(CancellationToken.None);

        Assert.Equal(3, folders.Count);

        Assert.Contains(folders, f => f.CategoryName == "General" && f.FolderName == "Service Bulletin"
            && f.Url == $"{BaseUrl}/support/solutions/folders/80000680701");
        Assert.Contains(folders, f => f.CategoryName == "FAQs QUEEN" && f.FolderName == "Queen - General"
            && f.Url == $"{BaseUrl}/support/solutions/folders/80000701915");
        Assert.Contains(folders, f => f.CategoryName == "FAQs QUEEN" && f.FolderName == "QUEEN - Electronics"
            && f.Url == $"{BaseUrl}/support/solutions/folders/80000722110");

        // Politeness: exactly one request (the homepage), fully accounted for.
        Assert.Single(handler.Requests);
        Assert.Equal(gate.Acquired.Count, gate.Reported.Count);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverArticlesInFolderAsync_MultiPageFolder_FollowsPaginationToCompletion()
    {
        const string folderUrl = $"{BaseUrl}/support/solutions/folders/80000432961";
        var folder = new FreshdeskFolder("FAQs ALIEN", "ALIEN - General", folderUrl);

        var (client, gate, handler) = BuildClient(h => h
            .MapHtml(folderUrl, FolderPage1Html)
            .MapHtml($"{folderUrl}/page/2", FolderPage2Html));

        var summaries = await client.DiscoverArticlesInFolderAsync(folder, CancellationToken.None);

        // Both pages' articles are present — pagination was followed to completion,
        // not stopped after the first page.
        Assert.Equal(3, summaries.Count);
        Assert.Contains(summaries, s => s.Title == "\"How-to\" rebuild machine - Alien 4.0");
        Assert.Contains(summaries, s => s.Title == "Alien Game rules");
        Assert.Contains(summaries, s => s.Title == "ALIEN - Rulesheet explanation");
        Assert.All(summaries, s => Assert.Equal(folder, s.Folder));

        // Two page fetches, both through the gate.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
    }

    [Fact]
    public async Task DiscoverArticlesInFolderAsync_SinglePageFolder_StopsAfterOneFetch()
    {
        const string folderUrl = $"{BaseUrl}/support/solutions/folders/80000722109";
        var folder = new FreshdeskFolder("FAQs ALIEN", "ALIEN - Electronics", folderUrl);

        var (client, _, handler) = BuildClient(h => h.MapHtml(folderUrl, SinglePageFolderHtml));

        var summaries = await client.DiscoverArticlesInFolderAsync(folder, CancellationToken.None);

        Assert.Single(summaries);
        Assert.Equal("Alien - Schematics", summaries[0].Title);

        // No pagination div present → exactly one fetch, no attempt at a page/2 request.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchArticleAsync_WithAttachment_ReturnsFullArticle()
    {
        const string articleUrl = $"{BaseUrl}/support/solutions/articles/80001073771-queen-pinball-technical-manual";
        var folder = new FreshdeskFolder("FAQs QUEEN", "Queen - General", $"{BaseUrl}/support/solutions/folders/80000701915");
        var summary = new FreshdeskArticleSummary("QUEEN Pinball - Technical Manual", articleUrl, folder);

        var (client, gate, handler) = BuildClient(h => h.MapHtml(articleUrl, ArticleHtml));

        var article = await client.FetchArticleAsync(summary, CancellationToken.None);

        Assert.NotNull(article);
        Assert.Equal("QUEEN Pinball - Technical Manual", article!.Title);
        Assert.Equal(articleUrl, article.Url);
        Assert.Equal(folder, article.Folder);
        Assert.Single(article.Attachments);
        Assert.Equal($"{BaseUrl}/helpdesk/attachments/80209470065", article.Attachments[0].Url);

        Assert.Single(handler.Requests);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task FetchArticleAsync_HttpFailure_ReturnsNull()
    {
        const string articleUrl = $"{BaseUrl}/support/solutions/articles/nonexistent";
        var folder = new FreshdeskFolder("General", "FAQ", $"{BaseUrl}/support/solutions/folders/80000242806");
        var summary = new FreshdeskArticleSummary("Nonexistent", articleUrl, folder);

        var (client, gate, _) = BuildClient(h => h.Map(articleUrl,
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));

        var article = await client.FetchArticleAsync(summary, CancellationToken.None);

        // Degrade visibly: HTTP failure logs and returns null, doesn't throw.
        Assert.Null(article);
        Assert.Single(gate.Acquired);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task DiscoverFoldersAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        var (client, gate, _) = BuildClient(h => h.MapHtml(SolutionsUrl, SolutionsHomeHtml));
        gate.ThrowOnAcquire = new PolitenessException(PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(
            () => client.DiscoverFoldersAsync(CancellationToken.None));
    }

    private static (FreshdeskSolutionsClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var options = Options.Create(new FreshdeskOptions { BaseUrl = BaseUrl });
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) };

        var client = new FreshdeskSolutionsClient(
            httpClient,
            gate,
            Options.Create(new PolitenessOptions()),
            options,
            NullLogger<FreshdeskSolutionsClient>.Instance);

        return (client, gate, handler);
    }
}
