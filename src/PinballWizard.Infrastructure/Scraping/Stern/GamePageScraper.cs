using System.Text.Json.Serialization;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Stern;

/// <summary>
/// Source 2: scrapes individual game pages at
/// <c>sternpinball.com/game/{slug}/</c>. Vue.js rendered — requires
/// Playwright. Walks 3 tabs per game page: Promotional Materials,
/// Game Code, Specs &amp; Manual. Also extracts structured game
/// metadata (editions, prices, features).
/// </summary>
/// <remarks>
/// Extends <see cref="PolitePlaywrightScraperBase"/>. Each game-page
/// load acquires a fresh politeness lease (per-origin throttle + delay
/// + robots.txt check); tab clicks reuse the open page (no extra
/// origin requests, lease held for the page's lifetime).
/// </remarks>
public sealed class GamePageScraper : PolitePlaywrightScraperBase, ISourceScraper
{
    private readonly GameListingScraper _listingScraper;

    /// <inheritdoc />
    public string Name => "Game Pages";
    public string Manufacturer => "Stern";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.Stern;

    /// <summary>Initializes a new <see cref="GamePageScraper"/>.</summary>
    public GamePageScraper(
        GameListingScraper listingScraper,
        PlaywrightFactory playwrightFactory,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ScraperSettings> settings,
        ILogger<GamePageScraper> logger)
        : base(playwrightFactory, politeness, politenessOptions.Value, logger,
               settings.Value.PlaywrightContextRecycleInterval)
    {
        ArgumentNullException.ThrowIfNull(listingScraper);
        ArgumentNullException.ThrowIfNull(settings);
        _listingScraper = listingScraper;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // First discover all game slugs
        var games = await _listingScraper.DiscoverGamesAsync(cancellationToken);
        Logger.LogInformation("Beginning to scrape {Count} game pages", games.Count);

        foreach (var game in games)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            Logger.LogInformation("Scraping game page: {Slug} ({Url})", game.Slug, game.GamePageUrl);

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
        try
        {
            await using var politePage = await NewPolitePageAsync(game.GamePageUrl, cancellationToken).ConfigureAwait(false);
            var page = politePage.Page;

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

            // Polite delay between game pages is handled by the gate on
            // the next NewPolitePageAsync acquire — no manual Task.Delay.
        }
        catch (PolitenessException)
        {
            // Bubble up — orchestrator handles source-level abort.
            throw;
        }
        catch (Exception ex)
        {
            // Broad catch: per-game-page failure must not abort the loop; OOM/cancellation
            // still propagate via the runtime. A single bad game page is logged and skipped.
            Logger.LogError(ex, "Failed to scrape game page: {Slug}", game.Slug);
        }

        return items;
    }

    /// <summary>
    /// Builds a <see cref="GameRecord"/> from the rendered HTML of the
    /// game page, preferring machine-consumer metadata (Open Graph,
    /// JSON-LD, the <c>contact-for-availability</c> shop links) over
    /// rendered-DOM scraping. See
    /// <see cref="StaticMetadataExtractor"/> and
    /// <c>docs/metadata-audit.md</c> Tier 2 for the rationale.
    /// </summary>
    /// <remarks>
    /// We feed Playwright's already-rendered HTML through AngleSharp
    /// rather than issuing a second HTTP request — single page load, more
    /// polite to Stern, and the static metadata is server-side-rendered
    /// so it's present in the rendered HTML regardless.
    /// </remarks>
    private async Task<GameRecord?> ExtractGameMetadataAsync(
        IPage page, DiscoveredGame game, CancellationToken cancellationToken)
    {
        try
        {
            var html = await page.ContentAsync();
            var parser = new HtmlParser();
            using var doc = parser.ParseDocument(html);

            var staticMeta = StaticMetadataExtractor.Extract(doc);

            // Title: prefer the static sources Stern publishes (form input,
            // og:title), fall back to rendered H1s, then page <title>, then
            // slug-cased. SanitizeGameTitle handles banner/CTA filtering.
            var titleCandidates = new List<string?> { staticMeta.Title };
            foreach (var h1 in doc.QuerySelectorAll("h1"))
            {
                var text = h1.TextContent?.Trim();
                if (!string.IsNullOrEmpty(text)) titleCandidates.Add(text);
            }

            var title = GamePageExtractors.SanitizeGameTitle(
                titleCandidates, doc.Title, game.Slug);

            if (staticMeta.Editions.Count == 0)
            {
                Logger.LogWarning(
                    "Static metadata extraction yielded 0 editions for {Slug} — Stern may have changed the contact-for-availability URL pattern. Catalog will record zero editions for this game.",
                    game.Slug);
            }

            var record = new GameRecord
            {
                GameId = GameRecord.GenerateId(game.Slug),
                Title = title,
                Slug = game.Slug,
                GamePageUrl = game.GamePageUrl,
                DiscoveredOn = game.DiscoveredOn,
                Editions = staticMeta.Editions,
                DatePublished = staticMeta.DatePublished,
                ReleaseYear = staticMeta.DatePublished?.Year,
                Source = new GameSourceInfo
                {
                    ScrapedFrom = game.GamePageUrl,
                    ScrapedAt = DateTime.UtcNow
                }
            };
            return ApplyPageContent(record, doc);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException
                                      or NullReferenceException or FormatException
                                      or System.Text.Json.JsonException)
        {
            Logger.LogWarning(ex, "Failed to extract metadata for game {Slug}", game.Slug);
            return null;
        }
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
                Logger.LogDebug("Tab '{Tab}' not found on {Slug}", tabName, game.Slug);
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

            Logger.LogDebug("Tab '{Tab}' on {Slug}: found {Count} links", tabName, game.Slug, links.Count);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException
                                      or NullReferenceException or FormatException
                                      or System.Text.Json.JsonException)
        {
            Logger.LogWarning(ex, "Failed to scrape tab '{Tab}' on {Slug}", tabName, game.Slug);
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
            catch (PlaywrightException)
            {
                // Try next selector — PlaywrightException is the realistic failure
                // when a selector doesn't match or the click is intercepted.
            }
        }

        return false;
    }

    // Pure mapping of rendered-page content onto the GameRecord. internal for
    // unit testing without driving Playwright (see GamePageScraperContentTests).
    internal static GameRecord ApplyPageContent(GameRecord record, AngleSharp.Dom.IDocument doc)
    {
        record.OverviewProse = GamePageContentExtractor.ExtractOverviewProse(doc);
        record.TrailerUrl = GamePageContentExtractor.ExtractTrailerUrl(doc);
        record.Accessories = GamePageContentExtractor.ExtractAccessories(doc);
        record.ShopCollectionUrl = GamePageContentExtractor.ExtractShopCollectionUrl(doc);
        return record;
    }

    // Class-with-settable-properties (not a positional record) because
    // Playwright's EvaluateAsync<T> deserializer (EvaluateArgumentValueConverter
    // .ToExpectedType, both in 1.12.0 and 1.59.0 confirmed) calls
    // Activator.CreateInstance(t) and then assigns properties one by one.
    // Positional records have no parameterless ctor, so Activator throws.
    //
    // PR #72 attempted to revert this to a positional record on the assumption
    // that Playwright 1.59 had switched to System.Text.Json deserialization;
    // that assumption was wrong (live-site validation surfaced
    // MissingMethodException at sternpinball.com against the bulletins page).
    // See docs/decision-log.md DL-0002.
    //
    // `internal` (not `private`) so SternPlaywrightDtoActivatorContractTests
    // in the test assembly can assert the parameterless-ctor + property-name
    // contract that Playwright's deserializer enforces at runtime.
    internal sealed class LinkRaw
    {
        [JsonPropertyName("href")] public string Href { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("isDownload")] public bool IsDownload { get; set; }
    }
}
