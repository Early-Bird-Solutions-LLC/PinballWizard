using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes articles from PinWiki via the MediaWiki API.
/// PinWiki has 500+ articles covering repair guides, game histories, technical specs,
/// maintenance, troubleshooting, and glossary terms.
/// API: https://www.pinwiki.com/wiki/api.php
/// </summary>
public sealed class PinWikiScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private readonly ILogger<PinWikiScraper> _logger;

    private const string ApiBase = "https://www.pinwiki.com/wiki/api.php";

    public string Name => "PinWiki";

    public PinWikiScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<PinWikiScraper> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Enumerating all PinWiki articles via MediaWiki API");

        var allPages = new List<MediaWikiPage>();
        string? continueToken = null;

        // Paginate through all pages using the allpages query
        do
        {
            var url = $"{ApiBase}?action=query&list=allpages&aplimit=500&format=json";
            if (continueToken is not null)
                url += $"&apcontinue={Uri.EscapeDataString(continueToken)}";

            MediaWikiQueryResponse? response;
            try
            {
                response = await _httpClient.GetFromJsonAsync<MediaWikiQueryResponse>(
                    url, new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Failed to query PinWiki allpages API");
                yield break;
            }

            if (response?.Query?.AllPages is not null)
            {
                allPages.AddRange(response.Query.AllPages);
            }

            continueToken = response?.Continue?.ApContinue;

        } while (continueToken is not null);

        _logger.LogInformation("PinWiki: found {Count} pages to process", allPages.Count);

        var count = 0;
        foreach (var page in allPages)
        {
            if (string.IsNullOrWhiteSpace(page.Title)) continue;

            // Skip special pages, talk pages, user pages
            if (page.Title.Contains(':') &&
                (page.Title.StartsWith("Talk:") ||
                 page.Title.StartsWith("User:") ||
                 page.Title.StartsWith("Template:") ||
                 page.Title.StartsWith("Category:") ||
                 page.Title.StartsWith("File:") ||
                 page.Title.StartsWith("MediaWiki:") ||
                 page.Title.StartsWith("Special:")))
                continue;

            var pageUrl = $"https://www.pinwiki.com/wiki/index.php/{Uri.EscapeDataString(page.Title.Replace(' ', '_'))}";
            var apiParseUrl = $"{ApiBase}?action=parse&page={Uri.EscapeDataString(page.Title)}&format=json&prop=text|categories";

            // Classify the article based on title
            var (docType, categories) = ClassifyArticle(page.Title);

            count++;
            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = apiParseUrl,
                    LinkText = page.Title,
                    DiscoveryContext = $"PinWiki article: {page.Title}"
                },
                SourceType = SourceType.PinWiki,
                DiscoveryUrl = pageUrl,
                DiscoveryContext = $"PinWiki: {page.Title}"
            };
        }

        _logger.LogInformation("PinWiki: discovered {Count} articles", count);
    }

    private static (DocumentType docType, List<ContentCategory> categories) ClassifyArticle(string title)
    {
        var titleLower = title.ToLowerInvariant();
        var categories = new List<ContentCategory>();

        // Repair/troubleshooting guides
        if (titleLower.Contains("repair") || titleLower.Contains("fix") ||
            titleLower.Contains("troubleshoot"))
        {
            categories.Add(ContentCategory.Repair);
            categories.Add(ContentCategory.Troubleshooting);
            return (DocumentType.RepairGuide, categories);
        }

        // Maintenance
        if (titleLower.Contains("maintenance") || titleLower.Contains("clean") ||
            titleLower.Contains("restore") || titleLower.Contains("restoration"))
        {
            categories.Add(ContentCategory.Maintenance);
            categories.Add(ContentCategory.Restoration);
            return (DocumentType.RepairGuide, categories);
        }

        // Glossary
        if (titleLower.Contains("glossary") || titleLower.Contains("terminology"))
        {
            categories.Add(ContentCategory.Glossary);
            return (DocumentType.Glossary, categories);
        }

        // Parts
        if (titleLower.Contains("parts") || titleLower.Contains("supplier"))
        {
            categories.Add(ContentCategory.PartsList);
            return (DocumentType.WikiArticle, categories);
        }

        // Schematics/wiring
        if (titleLower.Contains("schematic") || titleLower.Contains("wiring"))
        {
            categories.Add(ContentCategory.Schematics);
            categories.Add(ContentCategory.Wiring);
            return (DocumentType.WikiArticle, categories);
        }

        // History
        if (titleLower.Contains("history") || titleLower.Contains("timeline"))
        {
            categories.Add(ContentCategory.History);
            return (DocumentType.WikiArticle, categories);
        }

        // Default: wiki article with general content
        return (DocumentType.WikiArticle, categories);
    }
}

// MediaWiki API JSON models
internal sealed class MediaWikiQueryResponse
{
    [JsonPropertyName("query")]
    public MediaWikiQuery? Query { get; set; }

    [JsonPropertyName("continue")]
    public MediaWikiContinue? Continue { get; set; }
}

internal sealed class MediaWikiQuery
{
    [JsonPropertyName("allpages")]
    public List<MediaWikiPage>? AllPages { get; set; }
}

internal sealed class MediaWikiPage
{
    [JsonPropertyName("pageid")]
    public int PageId { get; set; }

    [JsonPropertyName("ns")]
    public int Namespace { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class MediaWikiContinue
{
    [JsonPropertyName("apcontinue")]
    public string? ApContinue { get; set; }

    [JsonPropertyName("continue")]
    public string? ContinueToken { get; set; }
}

internal sealed class MediaWikiParseResponse
{
    [JsonPropertyName("parse")]
    public MediaWikiParse? Parse { get; set; }
}

internal sealed class MediaWikiParse
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("pageid")]
    public int PageId { get; set; }

    [JsonPropertyName("text")]
    public MediaWikiText? Text { get; set; }
}

internal sealed class MediaWikiText
{
    [JsonPropertyName("*")]
    public string? Content { get; set; }
}
