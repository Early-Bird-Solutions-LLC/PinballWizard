using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes pinball manuals, schematics, and documentation from the Internet Archive.
/// Uses the Scrape API for cursor-based pagination (no 10k limit) and the Metadata API
/// for per-item file listings.
/// No authentication required for public collections.
/// </summary>
public sealed class InternetArchiveScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InternetArchiveScraper> _logger;

    private const string ScrapeApiUrl = "https://archive.org/services/search/v1/scrape";
    private const string MetadataApiUrl = "https://archive.org/metadata";
    private const string DownloadBaseUrl = "https://archive.org/download";
    private const string DetailsBaseUrl = "https://archive.org/details";

    /// <summary>
    /// Lucene queries targeting pinball-related collections and content.
    /// </summary>
    private static readonly string[] SearchQueries =
    [
        "pinball AND mediatype:texts AND (collection:manuals OR collection:manuals_various OR collection:arcademanuals)",
        "pinball AND schematic AND mediatype:texts",
        "pinball AND (manual OR operations) AND mediatype:texts AND -collection:manuals AND -collection:manuals_various AND -collection:arcademanuals",
    ];

    public string Name => "InternetArchive";

    public InternetArchiveScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<InternetArchiveScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Searching Internet Archive for pinball documentation");

        var seenIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalItems = 0;
        var totalFiles = 0;

        foreach (var query in SearchQueries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await foreach (var item in SearchItemsAsync(query, cancellationToken))
            {
                if (!seenIdentifiers.Add(item.Identifier ?? "")) continue;
                totalItems++;

                // Fetch file list for this item
                await foreach (var scraped in GetItemFilesAsync(item, cancellationToken))
                {
                    totalFiles++;
                    yield return scraped;
                }

                // Polite delay between metadata requests
                await Task.Delay(500, cancellationToken);
            }
        }

        _logger.LogInformation(
            "Internet Archive: discovered {Files} downloadable files across {Items} items",
            totalFiles, totalItems);
    }

    private async IAsyncEnumerable<IaScrapeItem> SearchItemsAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var pageCount = 0;

        do
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var url = $"{ScrapeApiUrl}?q={Uri.EscapeDataString(query)}" +
                      "&fields=identifier,title,creator,date,description" +
                      "&count=100";

            if (cursor is not null)
                url += $"&cursor={Uri.EscapeDataString(cursor)}";

            IaScrapeResponse? response;
            try
            {
                response = await _httpClient.GetFromJsonAsync<IaScrapeResponse>(
                    url, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Internet Archive search failed for query page {Page}", pageCount);
                yield break;
            }

            if (response?.Items is null or { Count: 0 })
                yield break;

            pageCount++;
            _logger.LogDebug("Internet Archive search page {Page}: {Count} items", pageCount, response.Items.Count);

            foreach (var item in response.Items)
            {
                yield return item;
            }

            cursor = response.Cursor;
            await Task.Delay(300, cancellationToken);

        } while (cursor is not null);
    }

    private async IAsyncEnumerable<ScrapedItem> GetItemFilesAsync(
        IaScrapeItem item,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Identifier)) yield break;

        List<IaFileEntry>? files;
        try
        {
            var response = await _httpClient.GetFromJsonAsync<IaFilesResponse>(
                $"{MetadataApiUrl}/{item.Identifier}", cancellationToken);
            files = response?.Files;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Failed to get metadata for {Id}", item.Identifier);
            yield break;
        }

        if (files is null) yield break;

        var title = item.GetTitle() ?? item.Identifier;
        var creator = item.GetCreator() ?? "Unknown";
        var detailsUrl = $"{DetailsBaseUrl}/{item.Identifier}";

        foreach (var file in files.Where(IsUsableFile))
        {
            var downloadUrl = $"{DownloadBaseUrl}/{item.Identifier}/{Uri.EscapeDataString(file.Name!)}";

            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = downloadUrl,
                    LinkText = $"{title} - {file.Name}",
                    DiscoveryContext = $"Internet Archive: {creator}",
                    GameSlug = GenerateSlug(title!)
                },
                SourceType = SourceType.InternetArchive,
                DiscoveryUrl = detailsUrl,
                DiscoveryContext = $"Internet Archive item: {title}"
            };
        }
    }

    private static bool IsUsableFile(IaFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(file.Name)) return false;
        if (file.Source == "derivative") return false;
        if (file.Name.EndsWith("_meta.xml") || file.Name.EndsWith("_files.xml")) return false;

        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        return ext is ".pdf" or ".txt" or ".html" or ".htm" or ".doc" or ".docx" or ".jpg" or ".png";
    }

    private static string GenerateSlug(string title)
    {
        var slug = title.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("'", "")
            .Replace("\"", "");

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Trim('-');
    }
}

// Internet Archive Scrape API response models
internal sealed class IaScrapeResponse
{
    [JsonPropertyName("items")]
    public List<IaScrapeItem>? Items { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }
}

internal sealed class IaScrapeItem
{
    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    [JsonPropertyName("title")]
    public JsonElement? Title { get; set; }

    [JsonPropertyName("creator")]
    public JsonElement? Creator { get; set; }

    [JsonPropertyName("date")]
    public JsonElement? Date { get; set; }

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }

    /// <summary>Extracts the first string value from a field that may be a string or array.</summary>
    public string? GetTitle() => ExtractString(Title);
    public string? GetCreator() => ExtractString(Creator);

    private static string? ExtractString(JsonElement? element)
    {
        if (element is null) return null;
        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Array when element.Value.GetArrayLength() > 0 => element.Value[0].GetString(),
            _ => element.Value.ToString()
        };
    }
}

internal sealed class IaFilesResponse
{
    [JsonPropertyName("files")]
    public List<IaFileEntry>? Files { get; set; }
}

internal sealed class IaFileEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
}
