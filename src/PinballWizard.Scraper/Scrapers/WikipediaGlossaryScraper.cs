using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes the Wikipedia Glossary of Pinball Terms via the MediaWiki API.
/// Extracts individual term definitions as separate discoverable items.
/// API: https://en.wikipedia.org/w/api.php
/// </summary>
public sealed class WikipediaGlossaryScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WikipediaGlossaryScraper> _logger;

    private const string PageTitle = "Glossary_of_pinball_terms";
    private const string ApiUrl = "https://en.wikipedia.org/w/api.php";
    private const string PageUrl = "https://en.wikipedia.org/wiki/Glossary_of_pinball_terms";

    public string Name => "WikipediaGlossary";

    public WikipediaGlossaryScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<WikipediaGlossaryScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching Wikipedia Glossary of Pinball Terms");

        // Fetch parsed HTML content via MediaWiki API
        var apiRequestUrl = $"{ApiUrl}?action=parse&page={PageTitle}&prop=text&format=json&formatversion=2";

        WikipediaParseResponse? response;
        try
        {
            response = await _httpClient.GetFromJsonAsync<WikipediaParseResponse>(
                apiRequestUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Wikipedia glossary");
            yield break;
        }

        var html = response?.Parse?.Text;
        if (string.IsNullOrWhiteSpace(html))
        {
            _logger.LogWarning("Wikipedia glossary returned empty content");
            yield break;
        }

        // First, yield the full glossary page as a single document
        yield return new ScrapedItem
        {
            Link = new DiscoveredLink
            {
                FileUrl = PageUrl,
                LinkText = "Glossary of Pinball Terms (Wikipedia)",
                DiscoveryContext = "Wikipedia Glossary"
            },
            SourceType = SourceType.WikipediaGlossary,
            DiscoveryUrl = PageUrl,
            DiscoveryContext = "Wikipedia Glossary of Pinball Terms"
        };

        // Extract individual glossary terms from <dt> definition list elements
        // Wikipedia glossaries use <dt>/<dd> pairs for term/definition
        var termPattern = new Regex(
            @"<dt[^>]*>.*?<(?:dfn|b|span)[^>]*>([^<]+)</(?:dfn|b|span)>.*?</dt>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var terms = new List<string>();
        foreach (Match match in termPattern.Matches(html))
        {
            var term = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(term))
                terms.Add(term);
        }

        // If <dt> extraction didn't find enough, try <b> tags within list items
        if (terms.Count < 10)
        {
            var boldPattern = new Regex(
                @"<b>([^<]{2,50})</b>",
                RegexOptions.IgnoreCase);

            terms.Clear();
            foreach (Match match in boldPattern.Matches(html))
            {
                var term = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(term) && !term.Contains('<'))
                    terms.Add(term);
            }
        }

        // Yield each term as a discoverable link anchored to its section
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (!seen.Add(term)) continue;

            var anchor = Uri.EscapeDataString(term.Replace(' ', '_'));
            var termUrl = $"{PageUrl}#{anchor}";

            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = termUrl,
                    LinkText = term,
                    DiscoveryContext = "Wikipedia Glossary Term"
                },
                SourceType = SourceType.WikipediaGlossary,
                DiscoveryUrl = PageUrl,
                DiscoveryContext = $"Glossary term: {term}"
            };
        }

        _logger.LogInformation("Wikipedia Glossary: discovered {Count} terms", seen.Count);
    }
}

// MediaWiki API response models
internal sealed class WikipediaParseResponse
{
    [JsonPropertyName("parse")]
    public WikipediaParsedContent? Parse { get; set; }
}

internal sealed class WikipediaParsedContent
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
