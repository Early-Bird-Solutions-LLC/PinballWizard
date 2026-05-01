using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PinballWizard.Scraper.Infrastructure;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Discovers all game slugs from /games/, /games/archive/, and /games/vault/.
/// These are Vue.js rendered pages requiring Playwright.
/// Returns a list of (slug, listing_source) pairs for GamePageScraper to process.
/// </summary>
public sealed class GameListingScraper
{
    private readonly PlaywrightFactory _playwrightFactory;
    private readonly ScraperSettings _settings;
    private readonly ILogger<GameListingScraper> _logger;

    private static readonly string[] ListingPaths =
    [
        "/games/",
        "/games/archive/",
        "/games/vault/"
    ];

    public GameListingScraper(
        PlaywrightFactory playwrightFactory,
        IOptions<ScraperSettings> settings,
        ILogger<GameListingScraper> logger)
    {
        _playwrightFactory = playwrightFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Discover all game slugs across all listing pages.
    /// Returns deduplicated slugs with which listing(s) they appeared on.
    /// </summary>
    public async Task<List<DiscoveredGame>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        var games = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ListingPaths)
        {
            var url = $"{_settings.BaseUrl}{path}";
            _logger.LogInformation("Discovering games from: {Url}", url);

            try
            {
                var slugs = await ScrapeListingPageAsync(url, cancellationToken);
                var listingName = path.Trim('/').Replace("games/", "").TrimEnd('/');
                if (string.IsNullOrEmpty(listingName)) listingName = "games_listing";

                foreach (var slug in slugs)
                {
                    if (games.TryGetValue(slug, out var existing))
                    {
                        existing.DiscoveredOn.Add(listingName);
                    }
                    else
                    {
                        games[slug] = new DiscoveredGame
                        {
                            Slug = slug,
                            GamePageUrl = $"{_settings.BaseUrl}/game/{slug}/",
                            DiscoveredOn = [listingName]
                        };
                    }
                }

                _logger.LogInformation("Found {Count} game slugs on {Path}", slugs.Count, path);

                // Polite delay between listing pages
                await Task.Delay(_settings.PageLoadDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scrape listing page: {Url}", url);
            }
        }

        _logger.LogInformation("Total unique games discovered: {Count}", games.Count);
        return [.. games.Values];
    }

    private async Task<List<string>> ScrapeListingPageAsync(string url, CancellationToken cancellationToken)
    {
        var page = await _playwrightFactory.NewPageAsync();
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });

            // Wait for Vue.js to render game cards
            await page.WaitForSelectorAsync("a[href*='/game/']", new PageWaitForSelectorOptions
            {
                Timeout = 15_000
            });

            // Extract all /game/{slug}/ links
            var hrefs = await page.EvalOnSelectorAllAsync<string[]>(
                "a[href*='/game/']",
                "elements => elements.map(el => el.getAttribute('href'))");

            var slugs = new List<string>();
            foreach (var href in hrefs)
            {
                if (string.IsNullOrWhiteSpace(href)) continue;
                var slug = ExtractSlug(href);
                if (slug is not null && !slugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    slugs.Add(slug);
                }
            }

            return slugs;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Extracts the game slug from a URL like "/game/stranger-things/" or
    /// "https://sternpinball.com/game/stranger-things/".
    /// </summary>
    private static string? ExtractSlug(string href)
    {
        // Match /game/{slug}/ pattern
        var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("game", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        return null;
    }
}

/// <summary>
/// A game discovered from a listing page, before visiting its individual game page.
/// </summary>
public sealed class DiscoveredGame
{
    public required string Slug { get; init; }
    public required string GamePageUrl { get; init; }
    public List<string> DiscoveredOn { get; init; } = [];
}
