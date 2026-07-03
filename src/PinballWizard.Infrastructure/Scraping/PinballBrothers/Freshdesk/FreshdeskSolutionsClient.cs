using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// HTTP client for the Pinball Brothers Freshdesk support portal
// (pinballbrothers.freshdesk.com). Crawls the live site fresh on every call —
// no hardcoded category/folder/article lists — so newly published content is
// always picked up. Selectors verified against real markup 2026-07-03:
//   - Category/folder list: div.cs-s > h3.heading > a (category name);
//     div.list-lead > a[href*='/support/solutions/folders/'] (folder name via
//     its title= attribute + href).
//   - Folder article list:  section.article-list.c-list > .c-row.c-article-row
//     > .article-title > a.c-link[href]; pagination via li.next:not(.disabled) a[href].
public sealed class FreshdeskSolutionsClient : PoliteScraperBase
{
    private readonly HttpClient _http;
    private readonly FreshdeskOptions _options;
    private static readonly HtmlParser Parser = new();

    public FreshdeskSolutionsClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<FreshdeskOptions> options,
        ILogger<FreshdeskSolutionsClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<FreshdeskFolder>> DiscoverFoldersAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{_options.BaseUrl}{_options.SolutionsHomePath}");
        var html = await GetStringPolitelyAsync(_http, url, cancellationToken).ConfigureAwait(false);

        using var doc = Parser.ParseDocument(html);
        var folders = new List<FreshdeskFolder>();

        foreach (var categoryEl in doc.QuerySelectorAll("div.cs-s"))
        {
            var categoryName = categoryEl.QuerySelector("h3.heading a")?.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(categoryName)) continue;

            foreach (var folderAnchor in categoryEl.QuerySelectorAll("div.list-lead a[href*='/support/solutions/folders/']"))
            {
                var href = folderAnchor.GetAttribute("href");
                var folderName = folderAnchor.GetAttribute("title");
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(folderName)) continue;

                folders.Add(new FreshdeskFolder(
                    CategoryName: categoryName,
                    FolderName: WebUtility.HtmlDecode(folderName),
                    Url: $"{_options.BaseUrl}{href}"));
            }
        }

        Logger.LogInformation("Freshdesk: discovered {Count} folder(s) across all categories.", folders.Count);
        return folders;
    }

    public async Task<IReadOnlyList<FreshdeskArticleSummary>> DiscoverArticlesInFolderAsync(
        FreshdeskFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var summaries = new List<FreshdeskArticleSummary>();
        string? pageUrl = folder.Url;

        while (pageUrl is not null)
        {
            var html = await GetStringPolitelyAsync(_http, new Uri(pageUrl), cancellationToken).ConfigureAwait(false);
            using var doc = Parser.ParseDocument(html);

            foreach (var anchor in doc.QuerySelectorAll("section.article-list.c-list a.c-link[href]"))
            {
                var href = anchor.GetAttribute("href");
                var title = anchor.TextContent.Trim();
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title)) continue;

                summaries.Add(new FreshdeskArticleSummary(
                    Title: WebUtility.HtmlDecode(title),
                    Url: $"{_options.BaseUrl}{href}",
                    Folder: folder));
            }

            // "Next" link is absent entirely on single-page folders, and
            // present-but-disabled (no href) on the last page of a
            // multi-page folder — both terminate the loop.
            var nextHref = doc.QuerySelector("li.next:not(.disabled) a[href]")?.GetAttribute("href");
            pageUrl = string.IsNullOrWhiteSpace(nextHref) ? null : $"{_options.BaseUrl}{nextHref}";
        }

        return summaries;
    }

    public async Task<FreshdeskArticle?> FetchArticleAsync(
        FreshdeskArticleSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, new Uri(summary.Url), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Freshdesk: failed to fetch article '{Url}'; skipping.", summary.Url);
            return null;
        }

        var extracted = FreshdeskArticleExtractor.Extract(html, summary.Url);
        if (extracted is null)
        {
            Logger.LogWarning("Freshdesk: could not extract content from article '{Url}'; skipping.", summary.Url);
            return null;
        }

        return new FreshdeskArticle
        {
            Title = extracted.Title,
            Url = summary.Url,
            Folder = summary.Folder,
            BodyText = extracted.BodyText,
            Attachments = extracted.Attachments,
        };
    }
}
