using System.Text.Json.Serialization;
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
/// Source 3: scrapes <c>sternpinball.com/support/service-bulletins/</c>.
/// Vue.js rendered — requires Playwright. Contains ~100+ technical
/// service bulletins; the page lazy-loads via scroll.
/// </summary>
/// <remarks>
/// Extends <see cref="PolitePlaywrightScraperBase"/>. Only one
/// politeness lease is taken (for the initial page load); subsequent
/// scroll events do not issue new origin requests in the polite sense
/// (the lazy-load fetches are XHR calls the browser makes
/// automatically, which the source's own throttle naturally bounds).
/// </remarks>
public sealed class ServiceBulletinScraper : PolitePlaywrightScraperBase, ISourceScraper
{
    private readonly ScraperSettings _settings;
    private const string BulletinsPath = "/support/service-bulletins/";

    /// <inheritdoc />
    public string Name => "Service Bulletins";
    public string Manufacturer => "Stern";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.Stern;

    /// <summary>Initializes a new <see cref="ServiceBulletinScraper"/>.</summary>
    public ServiceBulletinScraper(
        PlaywrightFactory playwrightFactory,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ScraperSettings> settings,
        ILogger<ServiceBulletinScraper> logger)
        : base(playwrightFactory, politeness, politenessOptions.Value, logger,
               settings.Value.PlaywrightContextRecycleInterval)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"{_settings.BaseUrl}{BulletinsPath}";
        Logger.LogInformation("Scraping service bulletins page: {Url}", url);

        await using var politePage = await NewPolitePageAsync(url, cancellationToken).ConfigureAwait(false);
        var page = politePage.Page;

        // Wait for Vue.js to render bulletin list
        await page.WaitForTimeoutAsync(3000);

        // Scroll to load all bulletins (they may be lazy-loaded or paginated)
        await ScrollToLoadAllAsync(page, cancellationToken);

        // Extract all bulletin entries
        var bulletins = await ExtractBulletinsAsync(page);
        Logger.LogInformation("Discovered {Count} service bulletins", bulletins.Count);

        foreach (var bulletin in bulletins)
        {
            yield return new ScrapedItem
            {
                Link = bulletin,
                SourceType = SourceType.ServiceBulletinPage,
                DiscoveryUrl = url,
                DiscoveryContext = "Service Bulletins Page"
            };
        }
    }

    private async Task ScrollToLoadAllAsync(IPage page, CancellationToken cancellationToken)
    {
        var previousCount = 0;
        var stableIterations = 0;

        for (int i = 0; i < 50; i++) // Safety limit
        {
            if (cancellationToken.IsCancellationRequested) break;

            await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await page.WaitForTimeoutAsync(1000);

            // Check if new content loaded
            var currentCount = await page.EvalOnSelectorAllAsync<int>(
                "a[href*='.pdf'], a[href*='wp-content/uploads']",
                "els => els.length");

            if (currentCount == previousCount)
            {
                stableIterations++;
                if (stableIterations >= 3) break; // No new content after 3 scrolls
            }
            else
            {
                stableIterations = 0;
                previousCount = currentCount;
            }

            // Also look for "Load More" or pagination buttons
            var loadMoreClicked = await TryClickLoadMoreAsync(page);
            if (loadMoreClicked)
            {
                await page.WaitForTimeoutAsync(2000);
                stableIterations = 0;
            }
        }
    }

    private static async Task<bool> TryClickLoadMoreAsync(IPage page)
    {
        var selectors = new[]
        {
            "button:has-text('Load More')",
            "button:has-text('Show More')",
            "a:has-text('Load More')",
            "[class*='load-more']",
            "[class*='pagination'] button:last-child"
        };

        foreach (var selector in selectors)
        {
            try
            {
                var element = await page.QuerySelectorAsync(selector);
                if (element is not null && await element.IsVisibleAsync())
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

    private static async Task<List<DiscoveredLink>> ExtractBulletinsAsync(IPage page)
    {
        var rawBulletins = await page.EvaluateAsync<BulletinRaw[]?>("""
            (() => {
                const bulletins = [];
                // Service bulletins are typically listed as links to PDFs
                const links = document.querySelectorAll(
                    'a[href*=".pdf"], a[href*="wp-content/uploads"]');

                for (const a of links) {
                    const href = a.href;
                    if (!href) continue;

                    const text = a.textContent?.trim() || '';

                    // Try to find a parent container with date/game info
                    const container = a.closest(
                        'tr, li, .bulletin, [class*="bulletin"], [class*="item"], article');

                    let date = null;
                    let relatedGames = null;

                    if (container) {
                        // Look for date in the container
                        const dateEl = container.querySelector(
                            'time, [class*="date"], td:nth-child(2)');
                        if (dateEl) date = dateEl.textContent?.trim();

                        // Look for game names in the container
                        const gameEl = container.querySelector(
                            '[class*="game"], td:nth-child(3), .games');
                        if (gameEl) relatedGames = gameEl.textContent?.trim();
                    }

                    bulletins.push({ href, text, date, relatedGames });
                }

                return bulletins.length > 0 ? bulletins : null;
            })()
        """);

        var links = new List<DiscoveredLink>();

        if (rawBulletins is null) return links;

        foreach (var raw in rawBulletins)
        {
            if (string.IsNullOrWhiteSpace(raw.Href)) continue;
            if (!raw.Href.Contains("sternpinball.com", StringComparison.OrdinalIgnoreCase)) continue;

            var context = "Service Bulletins Page";
            if (!string.IsNullOrWhiteSpace(raw.Date))
            {
                context += $" (date: {raw.Date})";
            }

            links.Add(new DiscoveredLink
            {
                FileUrl = raw.Href,
                LinkText = string.IsNullOrWhiteSpace(raw.Text) ? null : raw.Text,
                DiscoveryContext = context
            });
        }

        return links;
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
    internal sealed class BulletinRaw
    {
        [JsonPropertyName("href")] public string Href { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("relatedGames")] public string? RelatedGames { get; set; }
    }
}
