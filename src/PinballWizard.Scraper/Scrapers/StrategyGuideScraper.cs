using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes pinball strategy guides from multiple sources:
/// - System-J Pinball (systemjpinball.ca) — WordPress blog with detailed tactics pages
/// Strategy guides cover specific games with shot-by-shot breakdowns and scoring strategies.
/// </summary>
public sealed class StrategyGuideScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StrategyGuideScraper> _logger;

    public string Name => "StrategyGuides";

    /// <summary>
    /// Category/index pages to scrape for strategy guide links.
    /// </summary>
    private static readonly StrategySource[] Sources =
    [
        new("System-J Pinball", "https://systemjpinball.ca/index.php/category/strategy-guide/"),
    ];

    public StrategyGuideScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<StrategyGuideScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discovering pinball strategy guides");

        var totalCount = 0;

        foreach (var source in Sources)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var count = 0;
            await foreach (var item in ScrapeSourceAsync(source, cancellationToken))
            {
                count++;
                totalCount++;
                yield return item;
            }

            _logger.LogInformation("{Source}: discovered {Count} strategy guides", source.Name, count);
        }

        _logger.LogInformation("Strategy Guides: discovered {Count} total guides", totalCount);
    }

    private async IAsyncEnumerable<ScrapedItem> ScrapeSourceAsync(
        StrategySource source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string html;
        try
        {
            html = await _httpClient.GetStringAsync(source.IndexUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch strategy guide index: {Source}", source.Name);
            yield break;
        }

        // Check for WordPress pagination (next page links)
        var pages = new List<string> { source.IndexUrl };
        var pagePattern = new Regex(
            @"<a[^>]+class=""next page-numbers""[^>]+href=""([^""]+)""",
            RegexOptions.IgnoreCase);
        var pageMatch = pagePattern.Match(html);
        if (pageMatch.Success)
        {
            // Discover additional pages (limit to 10 pages to be safe)
            for (var i = 2; i <= 10; i++)
            {
                var nextUrl = $"{source.IndexUrl.TrimEnd('/')}page/{i}/";
                pages.Add(nextUrl);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pageUrl in pages)
        {
            if (cancellationToken.IsCancellationRequested) break;

            string pageHtml;
            if (pageUrl == source.IndexUrl)
            {
                pageHtml = html;
            }
            else
            {
                try
                {
                    pageHtml = await _httpClient.GetStringAsync(pageUrl, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    break; // No more pages
                }

                await Task.Delay(300, cancellationToken);
            }

            // Extract article links from WordPress post listings
            // Pattern: <h2 class="entry-title"><a href="...">Title</a></h2>
            // Also handles: <a href="..." rel="bookmark">Title</a>
            var articlePattern = new Regex(
                @"<a[^>]+href=""(https?://[^""]+)""[^>]*>([^<]*(?:tactics|strategy|tips|guide|links)[^<]*)</a>",
                RegexOptions.IgnoreCase);

            foreach (Match match in articlePattern.Matches(pageHtml))
            {
                var articleUrl = match.Groups[1].Value.Trim();
                var title = match.Groups[2].Value.Trim();

                if (string.IsNullOrWhiteSpace(title)) continue;
                if (!seen.Add(articleUrl)) continue;

                // Skip non-article links (category pages, tags, etc.)
                if (articleUrl.Contains("/category/") || articleUrl.Contains("/tag/")) continue;

                var gameName = ExtractGameName(title);

                yield return new ScrapedItem
                {
                    Link = new DiscoveredLink
                    {
                        FileUrl = articleUrl,
                        LinkText = title,
                        DiscoveryContext = $"Strategy Guide ({source.Name})",
                        GameSlug = GenerateSlug(gameName)
                    },
                    SourceType = SourceType.StrategyGuide,
                    DiscoveryUrl = source.IndexUrl,
                    DiscoveryContext = $"Strategy Guide: {gameName}"
                };
            }
        }
    }

    private static string ExtractGameName(string title)
    {
        // Remove common suffixes: "Tactics Page", "Links and Tips", "Strategy Guide"
        var name = Regex.Replace(title,
            @"\s*(tactics page|links and tips|links,? tips,? and strategy|strategy guide|full rules,? strategy,? and tips)\s*$",
            "",
            RegexOptions.IgnoreCase);

        return name.Trim();
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(":", "")
            .Replace("!", "");

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Trim('-');
    }

    private sealed record StrategySource(string Name, string IndexUrl);
}
