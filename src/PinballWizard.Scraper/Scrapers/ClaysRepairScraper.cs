using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes Clay's Pinball Repair Guides from pinrepair.com.
/// Nine comprehensive era-specific repair guides covering EM through modern solid-state.
/// All content is static HTML with no JavaScript rendering needed.
/// </summary>
public sealed class ClaysRepairScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaysRepairScraper> _logger;

    public string Name => "ClaysRepair";

    /// <summary>
    /// Known repair guide pages. These are stable URLs that have existed for 20+ years.
    /// </summary>
    private static readonly (string Url, string Title, string Era)[] Guides =
    [
        ("http://www.pinrepair.com/begin/index.htm", "Beginning Pinball Repair", "Prerequisites"),
        ("http://www.pinrepair.com/em/index1.htm", "EM Pinball Repair Guide", "1930s-1978"),
        ("http://www.pinrepair.com/sys37/index1.htm", "Williams System 3-7 Repair Guide", "1977-1984"),
        ("http://www.pinrepair.com/sys1/index.htm", "Gottlieb System 1 Repair Guide", "1978-1980"),
        ("http://www.pinrepair.com/sys3/index.htm", "Gottlieb System 3 Repair Guide", "1989-1996"),
        ("http://www.pinrepair.com/6803/index.htm", "Bally 6803 Repair Guide", "1985-1989"),
        ("http://www.pinrepair.com/gp/index.htm", "Gameplan Repair Guide", "1978-1985"),
        ("http://www.pinrepair.com/zac/index.htm", "Zaccaria Repair Guide", "1978-1985"),
        ("http://www.pinrepair.com/bell/index.htm", "Bell Nuova Repair Guide", "1986-1988"),
    ];

    public ClaysRepairScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<ClaysRepairScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discovering Clay's Pinball Repair Guides");

        var count = 0;

        foreach (var (url, title, era) in Guides)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Verify the page exists
            bool exists;
            try
            {
                using var response = await _httpClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Head, url), cancellationToken);
                exists = response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to verify guide: {Title}", title);
                continue;
            }

            if (!exists)
            {
                _logger.LogWarning("Guide not found: {Title} at {Url}", title, url);
                continue;
            }

            count++;
            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = url,
                    LinkText = $"{title} ({era})",
                    DiscoveryContext = "Clay's Pinball Repair Guides"
                },
                SourceType = SourceType.ClaysRepairGuides,
                DiscoveryUrl = "http://www.pinrepair.com/",
                DiscoveryContext = $"Repair Guide: {title} ({era})"
            };

            // Also discover sub-pages linked from the index
            await foreach (var subPage in DiscoverSubPagesAsync(url, title, cancellationToken))
            {
                count++;
                yield return subPage;
            }

            await Task.Delay(300, cancellationToken);
        }

        _logger.LogInformation("Clay's Repair: discovered {Count} guide pages", count);
    }

    private async IAsyncEnumerable<ScrapedItem> DiscoverSubPagesAsync(
        string indexUrl,
        string guideTitle,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string html;
        try
        {
            html = await _httpClient.GetStringAsync(indexUrl, cancellationToken);
        }
        catch (HttpRequestException)
        {
            yield break;
        }

        // Extract links to other pages within the same guide directory
        var baseUri = new Uri(indexUrl);
        var basePath = baseUri.GetLeftPart(UriPartial.Authority) +
                       baseUri.AbsolutePath[..baseUri.AbsolutePath.LastIndexOf('/')];

        var linkPattern = new Regex(
            @"href=""([^""]*\.(?:htm[l]?|pdf|txt))""",
            RegexOptions.IgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { indexUrl };

        foreach (Match match in linkPattern.Matches(html))
        {
            var href = match.Groups[1].Value;

            // Resolve relative URLs
            string fullUrl;
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl = href;
            }
            else
            {
                fullUrl = $"{basePath}/{href.TrimStart('/')}";
            }

            // Only include pages from the same guide directory
            if (!fullUrl.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(fullUrl)) continue;

            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = fullUrl,
                    LinkText = ExtractPageTitle(href),
                    DiscoveryContext = $"Clay's Repair: {guideTitle}"
                },
                SourceType = SourceType.ClaysRepairGuides,
                DiscoveryUrl = indexUrl,
                DiscoveryContext = $"Repair Guide sub-page: {guideTitle}"
            };
        }
    }

    private static string ExtractPageTitle(string href)
    {
        var filename = Path.GetFileNameWithoutExtension(href);
        // Convert "index2" -> "Page 2", "coils" -> "Coils"
        if (filename.StartsWith("index", StringComparison.OrdinalIgnoreCase) && filename.Length > 5)
            return $"Page {filename[5..]}";

        return filename.Length > 0
            ? char.ToUpper(filename[0]) + filename[1..].Replace('_', ' ').Replace('-', ' ')
            : filename;
    }
}
