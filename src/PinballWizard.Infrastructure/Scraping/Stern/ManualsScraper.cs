using AngleSharp;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;

using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Playwright;

namespace PinballWizard.Infrastructure.Scraping.Stern;

/// <summary>
/// Source 1: Scrapes sternpinball.com/manuals/ — static HTML page with ~148 manual PDFs.
/// Uses HttpClient + AngleSharp (no browser needed).
/// </summary>
public sealed class ManualsScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private readonly ILogger<ManualsScraper> _logger;
    private const string ManualsPath = "/manuals/";

    public string Name => "Manuals";

    public ManualsScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<ManualsScraper> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"{_settings.BaseUrl}{ManualsPath}";
        _logger.LogInformation("Scraping manuals page: {Url}", url);

        string html;
        try
        {
            html = await _httpClient.GetStringAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch manuals page: {Url}", url);
            yield break;
        }

        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken);

        // Find all links that point to PDF files
        var links = document.QuerySelectorAll("a[href]");
        var discoveredCount = 0;

        foreach (var link in links)
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            // Resolve relative URLs
            if (!Uri.TryCreate(new Uri(url), href, out var absoluteUri)) continue;
            var absoluteUrl = absoluteUri.ToString();

            // Only interested in PDF files from sternpinball.com
            if (!absoluteUrl.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!absoluteUrl.Contains("sternpinball.com", StringComparison.OrdinalIgnoreCase)) continue;

            var linkText = link.TextContent?.Trim();

            discoveredCount++;
            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = absoluteUrl,
                    LinkText = linkText,
                    DiscoveryContext = "Manuals Page"
                },
                SourceType = SourceType.ManualsPage,
                DiscoveryUrl = url,
                DiscoveryContext = "Manuals Page"
            };
        }

        _logger.LogInformation("Manuals page: discovered {Count} PDF links", discoveredCount);
    }
}
