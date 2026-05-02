using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Source 2: Scrapes individual game pages at sternpinball.com/game/{slug}/.
/// Vue.js rendered — requires Playwright. Walks 3 tabs per game page:
/// Promotional Materials, Game Code, Specs &amp; Manual.
/// Also extracts structured game metadata (editions, prices, features).
/// </summary>
public sealed class GamePageScraper : ISourceScraper
{
    private readonly GameListingScraper _listingScraper;
    private readonly PlaywrightFactory _playwrightFactory;
    private readonly ScraperSettings _settings;
    private readonly ILogger<GamePageScraper> _logger;

    public string Name => "Game Pages";

    public GamePageScraper(
        GameListingScraper listingScraper,
        PlaywrightFactory playwrightFactory,
        IOptions<ScraperSettings> settings,
        ILogger<GamePageScraper> logger)
    {
        _listingScraper = listingScraper;
        _playwrightFactory = playwrightFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // First discover all game slugs
        var games = await _listingScraper.DiscoverGamesAsync(cancellationToken);
        _logger.LogInformation("Beginning to scrape {Count} game pages", games.Count);

        foreach (var game in games)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            _logger.LogInformation("Scraping game page: {Slug} ({Url})", game.Slug, game.GamePageUrl);

            var items = await ScrapeGamePageAsync(game, cancellationToken);
            foreach (var item in items)
            {
                yield return item;
            }
        }
    }

    private async Task<List<ScrapedItem>> ScrapeGamePageAsync(DiscoveredGame game, CancellationToken cancellationToken)
    {
        var items = new List<ScrapedItem>();
        IPage? page = null;
        try
        {
            page = await _playwrightFactory.NewPageAsync();
            await page.GotoAsync(game.GamePageUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });

            // Allow Vue.js time to render
            await page.WaitForTimeoutAsync(2000);

            // Extract structured game metadata
            var gameRecord = await ExtractGameMetadataAsync(page, game, cancellationToken);
            if (gameRecord is not null)
            {
                items.Add(new ScrapedItem
                {
                    Game = gameRecord,
                    SourceType = SourceType.GamePage,
                    DiscoveryUrl = game.GamePageUrl,
                    DiscoveryContext = "Game Page"
                });
            }

            // Walk each tab and extract file links
            var tabs = new[]
            {
                (TabName: "Promotional Materials", Tab: GamePageTab.PromotionalMaterials),
                (TabName: "Game Code", Tab: GamePageTab.GameCode),
                (TabName: "Specs & Manual", Tab: GamePageTab.SpecsAndManual)
            };

            foreach (var (tabName, tab) in tabs)
            {
                var links = await ScrapeTabAsync(page, game, tabName, tab, cancellationToken);
                foreach (var link in links)
                {
                    items.Add(new ScrapedItem
                    {
                        Link = link,
                        SourceType = SourceType.GamePage,
                        DiscoveryUrl = game.GamePageUrl,
                        DiscoveryContext = $"Game Page → {tabName} tab"
                    });
                }
            }

            // Polite delay between game pages
            await Task.Delay(_settings.PageLoadDelayMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape game page: {Slug}", game.Slug);
        }
        finally
        {
            if (page is not null) await page.CloseAsync();
        }

        return items;
    }

    private async Task<GameRecord?> ExtractGameMetadataAsync(
        IPage page, DiscoveredGame game, CancellationToken cancellationToken)
    {
        try
        {
            // Pull every plausible title candidate from the DOM, then let
            // GamePageExtractors.SanitizeGameTitle reject banner/cookie text
            // and pick the first valid one.
            var candidates = await page.EvaluateAsync<string[]?>("""
                (() => {
                    const seen = new Set();
                    const out = [];
                    const push = (text) => {
                        const t = text?.trim();
                        if (t && !seen.has(t)) { seen.add(t); out.push(t); }
                    };
                    document.querySelectorAll('h1').forEach(el => push(el.textContent));
                    document.querySelectorAll('.game-title, [class*="GameTitle"], [class*="game-name"]')
                        .forEach(el => push(el.textContent));
                    return out;
                })()
            """);

            var pageTitle = await page.TitleAsync();
            var title = GamePageExtractors.SanitizeGameTitle(candidates, pageTitle, game.Slug);

            var record = new GameRecord
            {
                GameId = GameRecord.GenerateId(game.Slug),
                Title = title,
                Slug = game.Slug,
                GamePageUrl = game.GamePageUrl,
                DiscoveredOn = game.DiscoveredOn,
                Source = new GameSourceInfo
                {
                    ScrapedFrom = game.GamePageUrl,
                    ScrapedAt = DateTime.UtcNow
                }
            };

            // Try to extract edition information
            // Stern game pages typically show edition cards/sections with name, price, description
            var editions = await ExtractEditionsAsync(page);
            record.Editions = GamePageExtractors.DeduplicateEditions(editions);

            return record;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract metadata for game {Slug}", game.Slug);
            return null;
        }
    }

    private async Task<List<EditionInfo>> ExtractEditionsAsync(IPage page)
    {
        var editions = new List<EditionInfo>();

        try
        {
            // Stern game pages have edition sections — look for common patterns
            // The exact selectors may need tuning after initial testing
            var editionData = await page.EvaluateAsync<EditionRaw[]?>("""
                (() => {
                    const editions = [];
                    // Look for edition containers - common patterns on Stern game pages
                    const containers = document.querySelectorAll(
                        '[class*="edition"], [class*="model"], [class*="version"], ' +
                        '.product-option, .game-model');
                    
                    for (const container of containers) {
                        const name = container.querySelector(
                            'h2, h3, h4, [class*="name"], [class*="title"]')?.textContent?.trim();
                        const price = container.querySelector(
                            '[class*="price"], [class*="msrp"]')?.textContent?.trim();
                        const desc = container.querySelector(
                            'p, [class*="desc"], [class*="body"]')?.textContent?.trim();
                        
                        if (name) {
                            editions.push({ name, price: price || null, description: desc || null });
                        }
                    }
                    
                    // Fallback: look for Pro/Premium/LE text patterns in headings
                    if (editions.length === 0) {
                        const headings = document.querySelectorAll('h2, h3');
                        for (const h of headings) {
                            const text = h.textContent?.trim();
                            if (text && /\b(pro|premium|limited edition|le)\b/i.test(text)) {
                                editions.push({ name: text, price: null, description: null });
                            }
                        }
                    }
                    
                    return editions.length > 0 ? editions : null;
                })()
            """);

            if (editionData is not null)
            {
                foreach (var ed in editionData)
                {
                    editions.Add(new EditionInfo
                    {
                        Name = ed.Name,
                        Msrp = ed.Price,
                        Description = ed.Description
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract edition info (may not be present on page)");
        }

        return editions;
    }

    private async Task<List<DiscoveredLink>> ScrapeTabAsync(
        IPage page, DiscoveredGame game, string tabName, GamePageTab tab,
        CancellationToken cancellationToken)
    {
        var links = new List<DiscoveredLink>();

        try
        {
            // Click the tab — Stern uses various patterns for tab navigation
            var tabClicked = await ClickTabAsync(page, tabName);
            if (!tabClicked)
            {
                _logger.LogDebug("Tab '{Tab}' not found on {Slug}", tabName, game.Slug);
                return links;
            }

            // Wait for tab content to load
            await page.WaitForTimeoutAsync(1500);

            // Extract all downloadable links from the active tab content
            var rawLinks = await page.EvaluateAsync<LinkRaw[]?>("""
                (() => {
                    const links = [];
                    // Look for links in the currently visible/active tab content
                    const allLinks = document.querySelectorAll(
                        'a[href*=".pdf"], a[href*=".zip"], a[href*=".spk"], ' +
                        'a[href*="wp-content/uploads"], a[download]');
                    
                    for (const a of allLinks) {
                        // Check if the link is visible (in the active tab)
                        const rect = a.getBoundingClientRect();
                        if (rect.width === 0 && rect.height === 0) continue;
                        
                        const href = a.href;
                        const text = a.textContent?.trim() || a.getAttribute('title') || '';
                        const isDownload = a.hasAttribute('download');
                        
                        if (href && !href.startsWith('javascript:')) {
                            links.push({ href, text, isDownload });
                        }
                    }
                    return links.length > 0 ? links : null;
                })()
            """);

            if (rawLinks is not null)
            {
                foreach (var raw in rawLinks)
                {
                    if (string.IsNullOrWhiteSpace(raw.Href)) continue;

                    links.Add(new DiscoveredLink
                    {
                        FileUrl = raw.Href,
                        LinkText = string.IsNullOrWhiteSpace(raw.Text) ? null : raw.Text,
                        DiscoveryContext = $"Game Page → {tabName} tab",
                        GameSlug = game.Slug,
                        Tab = tab.ToString()
                    });
                }
            }

            _logger.LogDebug("Tab '{Tab}' on {Slug}: found {Count} links", tabName, game.Slug, links.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scrape tab '{Tab}' on {Slug}", tabName, game.Slug);
        }

        return links;
    }

    private static async Task<bool> ClickTabAsync(IPage page, string tabName)
    {
        // Try various selectors for tab navigation
        var selectors = new[]
        {
            $"button:has-text('{tabName}')",
            $"a:has-text('{tabName}')",
            $"[role='tab']:has-text('{tabName}')",
            $".tab:has-text('{tabName}')",
            $"[class*='tab']:has-text('{tabName}')",
            $"li:has-text('{tabName}')"
        };

        foreach (var selector in selectors)
        {
            try
            {
                var element = await page.QuerySelectorAsync(selector);
                if (element is not null)
                {
                    await element.ClickAsync();
                    return true;
                }
            }
            catch
            {
                // Try next selector
            }
        }

        return false;
    }

    private sealed class EditionRaw
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("price")] public string? Price { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class LinkRaw
    {
        [JsonPropertyName("href")] public string Href { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("isDownload")] public bool IsDownload { get; set; }
    }
}
