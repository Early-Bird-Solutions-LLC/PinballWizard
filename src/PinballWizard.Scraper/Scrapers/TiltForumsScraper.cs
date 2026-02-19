using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes game rulesheets from Tilt Forums (Discourse-based).
/// Starting from the Rulesheet Master List, discovers and downloads individual rulesheets.
/// Discourse provides a built-in JSON API — append .json to any URL.
/// </summary>
public sealed class TiltForumsScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private readonly ILogger<TiltForumsScraper> _logger;

    private const string MasterListUrl = "https://tiltforums.com/t/rulesheet-master-list/7230";

    public string Name => "TiltForums";

    public TiltForumsScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<TiltForumsScraper> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching Tilt Forums rulesheet master list");

        // Fetch the master list topic via Discourse JSON API
        var jsonUrl = $"{MasterListUrl}.json";
        DiscourseTopic? masterList;
        try
        {
            masterList = await _httpClient.GetFromJsonAsync<DiscourseTopic>(
                jsonUrl, new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Tilt Forums master list");
            yield break;
        }

        if (masterList?.PostStream?.Posts is null or { Count: 0 })
        {
            _logger.LogWarning("Master list returned no posts");
            yield break;
        }

        // The first post contains the master list with links to individual rulesheets
        var firstPost = masterList.PostStream.Posts[0];
        var html = firstPost.Cooked ?? "";

        // Extract links to rulesheet topics (pattern: /t/{slug}/{id})
        var linkPattern = new Regex(
            @"href=""(https?://tiltforums\.com/t/([^""]+?)/(\d+))""",
            RegexOptions.IgnoreCase);

        var matches = linkPattern.Matches(html);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (Match match in matches)
        {
            var topicUrl = match.Groups[1].Value;
            var topicSlug = match.Groups[2].Value;
            var topicId = match.Groups[3].Value;

            // Skip self-references and duplicates
            if (topicUrl.Contains("/7230")) continue;
            if (!seen.Add(topicUrl)) continue;

            // Extract game name from the slug (e.g., "iron-maiden-rulesheet" -> "Iron Maiden")
            var gameName = ExtractGameName(topicSlug);

            count++;
            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = $"{topicUrl}.json",
                    LinkText = gameName,
                    DiscoveryContext = "Tilt Forums Rulesheet Master List",
                    GameSlug = topicSlug
                },
                SourceType = SourceType.TiltForums,
                DiscoveryUrl = MasterListUrl,
                DiscoveryContext = $"Rulesheet: {gameName}"
            };
        }

        // Also extract any direct links in the post that aren't topic links
        // (some rulesheets are hosted externally, e.g., Google Docs)
        var externalPattern = new Regex(
            @"href=""(https?://(?!tiltforums\.com)[^""]+)""[^>]*>([^<]+)<",
            RegexOptions.IgnoreCase);

        foreach (Match match in externalPattern.Matches(html))
        {
            var externalUrl = match.Groups[1].Value;
            var linkText = match.Groups[2].Value.Trim();

            if (string.IsNullOrWhiteSpace(linkText)) continue;
            if (!seen.Add(externalUrl)) continue;

            // Skip non-rulesheet links
            if (externalUrl.Contains("imgur.com") || externalUrl.Contains("youtube.com")) continue;

            count++;
            yield return new ScrapedItem
            {
                Link = new DiscoveredLink
                {
                    FileUrl = externalUrl,
                    LinkText = linkText,
                    DiscoveryContext = "Tilt Forums Rulesheet Master List (external)"
                },
                SourceType = SourceType.TiltForums,
                DiscoveryUrl = MasterListUrl,
                DiscoveryContext = $"External rulesheet: {linkText}"
            };
        }

        _logger.LogInformation("Tilt Forums: discovered {Count} rulesheet links", count);
    }

    private static string ExtractGameName(string slug)
    {
        // Remove common suffixes
        var name = Regex.Replace(slug, @"-(rulesheet|rules|rule-sheet)$", "", RegexOptions.IgnoreCase);

        // Convert hyphens to spaces and title-case
        var words = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w =>
            w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}

// Discourse JSON API models
internal sealed class DiscourseTopic
{
    [JsonPropertyName("post_stream")]
    public DiscoursePostStream? PostStream { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }
}

internal sealed class DiscoursePostStream
{
    [JsonPropertyName("posts")]
    public List<DiscoursePost>? Posts { get; set; }
}

internal sealed class DiscoursePost
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("cooked")]
    public string? Cooked { get; set; }

    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    [JsonPropertyName("post_number")]
    public int PostNumber { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
