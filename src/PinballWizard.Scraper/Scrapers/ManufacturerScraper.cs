using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes manuals and documentation from non-Stern pinball manufacturer support pages.
/// Covers: Spooky Pinball, American Pinball, Chicago Gaming Company.
/// Each manufacturer has a support page with direct PDF download links.
/// Jersey Jack is excluded (no direct PDF links on their support page).
/// </summary>
public sealed class ManufacturerScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ManufacturerScraper> _logger;

    public string Name => "Manufacturers";

    private static readonly ManufacturerSource[] Sources =
    [
        new(
            "Spooky Pinball",
            "https://www.spookypinball.com/game-support/",
            @"href=""(https?://www\.spookypinball\.com/wp-content/uploads/[^""]+\.pdf)""",
            "spooky"
        ),
        new(
            "American Pinball",
            "https://www.american-pinball.com/support/",
            @"href=""(https?://[^""]+\.pdf)""",
            "american-pinball"
        ),
        new(
            "Chicago Gaming",
            "https://www.chicago-gaming.com/product/manuals/coin-op-pinball",
            @"href=""([^""]+\.pdf)""",
            "chicago-gaming"
        ),
    ];

    public ManufacturerScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<ManufacturerScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discovering pinball manufacturer documentation");

        var totalCount = 0;

        foreach (var source in Sources)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var count = 0;
            await foreach (var item in ScrapeManufacturerAsync(source, cancellationToken))
            {
                count++;
                totalCount++;
                yield return item;
            }

            _logger.LogInformation("{Manufacturer}: discovered {Count} documents", source.Name, count);
            await Task.Delay(500, cancellationToken);
        }

        _logger.LogInformation("Manufacturers: discovered {Count} total documents", totalCount);
    }

    private async IAsyncEnumerable<ScrapedItem> ScrapeManufacturerAsync(
        ManufacturerSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string html;
        try
        {
            html = await _httpClient.GetStringAsync(source.SupportUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Manufacturer} support page", source.Name);
            yield break;
        }

        var linkPattern = new Regex(source.PdfPattern, RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in linkPattern.Matches(html))
        {
            var pdfUrl = match.Groups[1].Value.Trim();

            // Resolve relative URLs
            if (!pdfUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var baseUri = new Uri(source.SupportUrl);
                pdfUrl = new Uri(baseUri, pdfUrl).AbsoluteUri;
            }

            if (!seen.Add(pdfUrl)) continue;

            // Skip non-pinball PDFs (catalogs, general company docs)
            var filename = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(pdfUrl));

            // Extract a human-readable title from the filename
            var title = CleanFilenameToTitle(filename);

            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = pdfUrl,
                    LinkText = $"{source.Name}: {title}",
                    DiscoveryContext = $"{source.Name} Support Page",
                    GameSlug = $"{source.Slug}_{GenerateSlug(title)}"
                },
                SourceType = SourceType.ManufacturerSite,
                DiscoveryUrl = source.SupportUrl,
                DiscoveryContext = $"{source.Name} documentation: {title}"
            };
        }
    }

    private static string CleanFilenameToTitle(string filename)
    {
        // Replace common separators with spaces
        var title = filename
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace("%20", " ");

        // Remove version/revision suffixes for cleaner titles
        title = Regex.Replace(title, @"\s*(Rev|rev|REV)\s*[\d_.]+\s*$", "");
        title = Regex.Replace(title, @"\s*(web|WEB)\s*$", "");

        // Title case
        title = Regex.Replace(title, @"\b\w", m => m.Value.ToUpperInvariant());

        return title.Trim();
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

    private sealed record ManufacturerSource(
        string Name,
        string SupportUrl,
        string PdfPattern,
        string Slug);
}
