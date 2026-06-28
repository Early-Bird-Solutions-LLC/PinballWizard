using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.ChicagoGaming;

/// <summary>
/// Chicago Gaming Company game-page scraper. Discovers CGC machines
/// via the <c>/coinop/</c> index page, then fetches each canonical
/// machine page and yields:
/// <list type="bullet">
///   <item>One <see cref="ScrapedItem"/> with <c>.Game</c> populated.</item>
///   <item>One <see cref="ScrapedItem"/> per same-host PDF found on the page.</item>
/// </list>
/// </summary>
/// <remarks>
/// CGC produces "Remake" editions of classic Bally/Williams machines
/// (Attack from Mars, Medieval Madness, Monster Bash, Cactus Canyon,
/// Pulp Fiction). The index page is the canonical filter — the
/// site's sitemap is incomplete in practice. CGC pages don't expose
/// JSON-LD product schema; the extractor relies on DOM heuristics
/// (page <c>&lt;title&gt;</c> with manufacturer suffix stripped, h1
/// fallback, prettified slug fallback).
/// <para>
/// Politeness, robots.txt, and 429 backoff are inherited from
/// <see cref="PoliteScraperBase"/>. CGC's robots.txt blocks
/// <c>/images</c> for the generic <c>User-agent: *</c>; we don't
/// fetch images, so the policy is honored automatically by the
/// gate.
/// </para>
/// </remarks>
public sealed class CgcGamePageScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly CgcMenuClient _menuClient;

    /// <inheritdoc />
    public string Name => "Chicago Gaming";
    public string Manufacturer => "Chicago Gaming";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.Cgc;

    /// <summary>Initializes a new <see cref="CgcGamePageScraper"/>.</summary>
    public CgcGamePageScraper(
        HttpClient httpClient,
        CgcMenuClient menuClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<CgcGamePageScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(menuClient);
        _httpClient = httpClient;
        _menuClient = menuClient;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Chicago Gaming scraper starting");

        List<Uri> machineUrls;
        try
        {
            machineUrls = await _menuClient.DiscoverMachineUrlsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Chicago Gaming menu discovery failed; aborting Chicago Gaming scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Chicago Gaming: {Count} machine URL(s) to process", machineUrls.Count);

        foreach (var machineUrl in machineUrls)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var (gameRecord, downloads) = await TryExtractAsync(machineUrl, cancellationToken).ConfigureAwait(false);
            if (gameRecord is not null)
            {
                yield return new ScrapedItem
                {
                    Game = gameRecord,
                    SourceType = SourceType.ChicagoGamingGamePage,
                    DiscoveryUrl = machineUrl.ToString(),
                    DiscoveryContext = "Chicago Gaming Game Page",
                };
            }

            foreach (var link in downloads)
            {
                yield return new ScrapedItem
                {
                    Link = link,
                    SourceType = SourceType.ChicagoGamingGamePage,
                    DiscoveryUrl = machineUrl.ToString(),
                    DiscoveryContext = "Chicago Gaming Game Page",
                };
            }
        }

        Logger.LogInformation("Chicago Gaming scraper complete");
    }

    private async Task<(GameRecord? Game, IReadOnlyList<DiscoveredLink> Downloads)> TryExtractAsync(
        Uri machineUrl, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, machineUrl, cancellationToken).ConfigureAwait(false);
            var record = CgcGamePageExtractor.ExtractGame(html, machineUrl);
            var downloads = CgcGamePageExtractor.ExtractDownloads(html, machineUrl);
            return (record, downloads);
        }
        catch (PolitenessException)
        {
            // Bubble up — orchestrator handles source-level abort.
            throw;
        }
        catch (Exception ex)
        {
            // Broad catch: per-URL failure must not abort the loop; OOM/cancellation still
            // propagate via the runtime. One bad page is logged and skipped.
            Logger.LogWarning(ex, "Chicago Gaming scraper: failed to fetch / extract {Url}; skipping.", machineUrl);
            return (null, []);
        }
    }
}
