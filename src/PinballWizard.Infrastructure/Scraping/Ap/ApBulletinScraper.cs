using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Ap;

// Discovers AP service bulletin PDFs from the static /support/ page.
// The page is server-rendered HTML (no SPA / Playwright needed). Bulletin PDFs
// live exclusively on the s4.american-pinball.com CDN subdomain.
public sealed class ApBulletinScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ApOptions _apOptions;

    public string Name => "American Pinball Bulletins";
    public string Manufacturer => "American Pinball";
    public string SourceId => IngestionSourceIds.ApBulletins;

    public ApBulletinScraper(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ApOptions> apOptions,
        ILogger<ApBulletinScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apOptions);
        _httpClient = httpClient;
        _apOptions = apOptions.Value;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("American Pinball bulletin scraper starting");

        var supportPageUrl = new Uri(new Uri(_apOptions.BaseUrl), "support/");

        List<DiscoveredLink> bulletins;
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, supportPageUrl, cancellationToken).ConfigureAwait(false);
            bulletins = ApBulletinExtractor.ExtractBulletins(html, supportPageUrl);
        }
        catch (PolitenessException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AP bulletin scraper: failed to fetch {Url}; aborting.", supportPageUrl);
            yield break;
        }

        Logger.LogInformation("American Pinball bulletin scraper: {Count} bulletin PDFs discovered", bulletins.Count);

        foreach (var link in bulletins)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            yield return new ScrapedItem
            {
                Link = link,
                SourceType = SourceType.AmericanPinballBulletinPage,
                DiscoveryUrl = supportPageUrl.ToString(),
                DiscoveryContext = ApBulletinExtractor.DiscoveryCtx,
            };
        }

        Logger.LogInformation("American Pinball bulletin scraper complete");
    }
}
