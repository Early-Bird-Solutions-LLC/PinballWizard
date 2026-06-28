using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Stern;

/// <summary>
/// Source 1: scrapes <c>sternpinball.com/manuals/</c> — static HTML page
/// with ~148 manual PDFs. Uses HttpClient + AngleSharp (no browser
/// needed).
/// </summary>
/// <remarks>
/// Extends <see cref="PoliteScraperBase"/> so every outbound request
/// flows through the politeness gate (robots.txt check, per-origin
/// throttle, 429 backoff). Per the locked feedback memory
/// <c>feedback_polite_scraping.md</c>, the politeness must be visibly
/// enforced — extending the base is the visible enforcement.
/// </remarks>
public sealed class ManualsScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private const string ManualsPath = "/manuals/";

    /// <inheritdoc />
    public string Name => "Manuals";
    public string Manufacturer => "Stern";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.Stern;

    /// <summary>Initializes a new <see cref="ManualsScraper"/>.</summary>
    public ManualsScraper(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ScraperSettings> settings,
        ILogger<ManualsScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(settings);
        _httpClient = httpClient;
        _settings = settings.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = new Uri($"{_settings.BaseUrl}{ManualsPath}");
        Logger.LogInformation("Scraping manuals page: {Url}", url);

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_httpClient, url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "Failed to fetch manuals page: {Url}", url);
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
            if (!Uri.TryCreate(url, href, out var absoluteUri)) continue;
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
                DiscoveryUrl = url.ToString(),
                DiscoveryContext = "Manuals Page"
            };
        }

        Logger.LogInformation("Manuals page: discovered {Count} PDF links", discoveredCount);
    }
}
