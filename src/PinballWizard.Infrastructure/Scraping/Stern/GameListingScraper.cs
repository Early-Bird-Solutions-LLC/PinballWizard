using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Stern;

/// <summary>
/// Discovers all game slugs from <c>/games/</c>, <c>/games/archive/</c>,
/// and <c>/games/vault/</c>. These are Vue.js rendered pages requiring
/// Playwright. Returns a list of (slug, listing source) pairs for
/// <see cref="GamePageScraper"/> to process.
/// </summary>
/// <remarks>
/// Extends <see cref="PolitePlaywrightScraperBase"/> so every page
/// load goes through the politeness gate. The shared
/// <c>BrowserContext</c> is reused across all three listing pages
/// (one context, three navigations) instead of one fresh context per
/// page.
/// </remarks>
public sealed class GameListingScraper : PolitePlaywrightScraperBase
{
    private readonly ScraperSettings _settings;

    private static readonly string[] ListingPaths =
    [
        "/games/",
        "/games/archive/",
        "/games/vault/"
    ];

    /// <summary>Initializes a new <see cref="GameListingScraper"/>.</summary>
    public GameListingScraper(
        PlaywrightFactory playwrightFactory,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ScraperSettings> settings,
        ILogger<GameListingScraper> logger)
        : base(playwrightFactory, politeness, politenessOptions.Value, logger,
               ResolveRecycleInterval(settings))
    {
        _settings = settings.Value;
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
            Logger.LogInformation("Discovering games from: {Url}", url);

            try
            {
                var slugs = await ScrapeListingPageAsync(url, cancellationToken).ConfigureAwait(false);
                var listingName = path.Trim('/').Replace("games/", "", StringComparison.Ordinal).TrimEnd('/');
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

                Logger.LogInformation("Found {Count} game slugs on {Path}", slugs.Count, path);

                // Polite delay between listing pages is handled by the
                // gate on the next NewPolitePageAsync acquire — no manual
                // Task.Delay here.
            }
            catch (Exception ex)
            {
                // Broad catch: per-listing-page failure must not abort discovery; OOM/cancellation
                // still propagate via the runtime. Other listing paths continue.
                Logger.LogError(ex, "Failed to scrape listing page: {Url}", url);
            }
        }

        Logger.LogInformation("Total unique games discovered: {Count}", games.Count);
        return [.. games.Values];
    }

    private async Task<List<string>> ScrapeListingPageAsync(string url, CancellationToken cancellationToken)
    {
        await using var politePage = await NewPolitePageAsync(url, cancellationToken).ConfigureAwait(false);
        var page = politePage.Page;

        // Wait for Vue.js to render game cards
        await page.WaitForSelectorAsync("a[href*='/game/']", new Microsoft.Playwright.PageWaitForSelectorOptions
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
    /// <summary>Game slug as it appears in the URL (e.g., "stranger-things").</summary>
    public required string Slug { get; init; }

    /// <summary>Absolute URL of the game's individual page.</summary>
    public required string GamePageUrl { get; init; }

    /// <summary>Which listing page(s) this slug appeared on (e.g., "games_listing", "archive", "vault").</summary>
    public List<string> DiscoveredOn { get; init; } = [];
}
