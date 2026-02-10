using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes rulesheets from the Pinball Archive (pinball.org/rules/).
/// One of the oldest pinball resources on the internet, hosting plain-text and HTML rulesheets
/// for games spanning all eras. Many sheets predate the web.
/// </summary>
public sealed class PinballArchiveScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PinballArchiveScraper> _logger;

    private const string IndexUrl = "https://www.pinball.org/rules/";

    public string Name => "PinballArchive";

    public PinballArchiveScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<PinballArchiveScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching Pinball Archive rulesheet index");

        string html;
        try
        {
            html = await _httpClient.GetStringAsync(IndexUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Pinball Archive index");
            yield break;
        }

        // Extract links to rulesheets — both local (.html/.txt) and external
        var linkPattern = new Regex(
            @"<a\s+[^>]*href=""([^""]+)""[^>]*>([^<]*)</a>",
            RegexOptions.IgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (Match match in linkPattern.Matches(html))
        {
            var href = match.Groups[1].Value.Trim();
            var linkText = match.Groups[2].Value.Trim();

            // Skip navigation and non-content links
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (href.StartsWith("#")) continue;
            if (href.StartsWith("mailto:")) continue;
            if (href.Contains("pinball.org/rules/") && href.EndsWith("/")) continue;

            // Resolve to full URL
            string fullUrl;
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                fullUrl = href;
            }
            else
            {
                fullUrl = href.StartsWith('/')
                    ? $"https://www.pinball.org{href}"
                    : $"{IndexUrl}{href}";
            }

            // Only include content files (.html, .htm, .txt, .pdf) and external rulesheet links
            var isLocalContent = fullUrl.StartsWith(IndexUrl, StringComparison.OrdinalIgnoreCase) &&
                                 (fullUrl.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                                  fullUrl.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                                  fullUrl.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                                  fullUrl.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

            var isExternal = !fullUrl.StartsWith("https://www.pinball.org", StringComparison.OrdinalIgnoreCase) &&
                             !fullUrl.StartsWith("http://www.pinball.org", StringComparison.OrdinalIgnoreCase);

            if (!isLocalContent && !isExternal) continue;
            if (!seen.Add(fullUrl)) continue;

            // Skip non-rulesheet external links
            if (isExternal && !IsLikelyRulesheetLink(fullUrl, linkText)) continue;

            var gameName = isLocalContent ? ExtractGameName(href) : linkText;

            count++;
            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = fullUrl,
                    LinkText = string.IsNullOrWhiteSpace(linkText) ? gameName : linkText,
                    DiscoveryContext = isExternal
                        ? "Pinball Archive (external link)"
                        : "Pinball Archive Rulesheet",
                    GameSlug = GenerateSlug(gameName)
                },
                SourceType = SourceType.PinballArchive,
                DiscoveryUrl = IndexUrl,
                DiscoveryContext = $"Rulesheet: {gameName}"
            };
        }

        _logger.LogInformation("Pinball Archive: discovered {Count} rulesheets", count);
    }

    private static bool IsLikelyRulesheetLink(string url, string linkText)
    {
        var combined = $"{url} {linkText}".ToLowerInvariant();
        return combined.Contains("rule") ||
               combined.Contains("rulesheet") ||
               combined.Contains("pinball") ||
               url.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractGameName(string href)
    {
        var filename = Path.GetFileNameWithoutExtension(href);
        // Remove common suffixes like "-notes", "2" for alternate versions
        filename = Regex.Replace(filename, @"[-_]?(notes|rules|rulesheet|\d+)$", "",
            RegexOptions.IgnoreCase);

        // Convert to title case
        var words = filename.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w =>
            w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("'", "")
            .Replace("\"", "");

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Trim('-');
    }
}
