using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// Discovers PDF/file attachments (Manuals, Rulebooks, Schematics, Service
// Bulletins) on the Pinball Brothers Freshdesk support portal and yields a
// ScrapedItem per attachment. Text-only articles (no attachment) are skipped
// here — they flow through PbFreshdeskArticleSynthesizer instead (Task 7/8).
public sealed class PbFreshdeskDocumentScraper : PoliteScraperBase, ISourceScraper
{
    // Category-name substrings that identify a specific machine. Matched
    // against category names like "FAQs QUEEN" / "FAQ PREDATOR" — deliberately
    // substring-based since Pinball Brothers is inconsistent about the
    // "FAQ" vs "FAQs" prefix and singular/plural. Public so the
    // --sync-pb-freshdesk-articles CLI verb (Program.cs) can reuse the same
    // list rather than maintaining its own copy that could drift out of sync
    // when Pinball Brothers adds a machine.
    public static readonly string[] KnownGameSlugs = ["alien", "queen", "abba", "predator"];

    private readonly FreshdeskSolutionsClient _client;

    public string Name => "Pinball Brothers Freshdesk Documents";
    public string Manufacturer => "Pinball Brothers";
    public string SourceId => IngestionSourceIds.PinballBrothersFreshdesk;

    public PbFreshdeskDocumentScraper(
        FreshdeskSolutionsClient client,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<PbFreshdeskDocumentScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Pinball Brothers Freshdesk document scraper starting");

        var folders = await TryDiscoverFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (folders is null) yield break;

        foreach (var folder in folders)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var summaries = await TryDiscoverArticlesAsync(folder, cancellationToken).ConfigureAwait(false);

            foreach (var summary in summaries)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                var article = await _client.FetchArticleAsync(summary, cancellationToken).ConfigureAwait(false);
                if (article is null || article.Attachments.Count == 0) continue;

                var gameSlug = MatchGameSlug(folder.CategoryName);
                var discoveryContext = $"Freshdesk Support Portal — {folder.FolderName}";

                foreach (var attachment in article.Attachments)
                {
                    yield return new ScrapedItem
                    {
                        Link = new DiscoveredLink
                        {
                            FileUrl = attachment.Url,
                            LinkText = article.Title,
                            DiscoveryContext = discoveryContext,
                            GameSlug = gameSlug,
                        },
                        SourceType = SourceType.PinballBrothersFreshdeskArticle,
                        DiscoveryUrl = article.Url,
                        DiscoveryContext = discoveryContext,
                    };
                }
            }
        }

        Logger.LogInformation("Pinball Brothers Freshdesk document scraper complete");
    }

    // Factored out of the iterator body per the codebase's established
    // pattern (see PbGamePageDocumentScraper.TryExtractLinks): a try/catch
    // around a yield-containing block is disallowed by the C# iterator
    // rules, so per-source-page failures are caught here and reported as a
    // sentinel (null / empty list) that the iterator can branch on freely.
    private async Task<List<FreshdeskFolder>?> TryDiscoverFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _client.DiscoverFoldersAsync(cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Pinball Brothers Freshdesk document scraper: folder discovery failed; aborting for this run.");
            return null;
        }
    }

    private async Task<List<FreshdeskArticleSummary>> TryDiscoverArticlesAsync(
        FreshdeskFolder folder, CancellationToken cancellationToken)
    {
        try
        {
            return (await _client.DiscoverArticlesInFolderAsync(folder, cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogWarning(ex,
                "Pinball Brothers Freshdesk document scraper: article discovery failed for folder '{Folder}'; skipping this folder.",
                folder.FolderName);
            return [];
        }
    }

    private static string? MatchGameSlug(string categoryName)
    {
        var lower = categoryName.ToLowerInvariant();
        return KnownGameSlugs.FirstOrDefault(slug => lower.Contains(slug, StringComparison.Ordinal));
    }
}
