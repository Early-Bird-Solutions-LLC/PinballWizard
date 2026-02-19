using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballApi;
using PinballApi.Models.WPPR.v2.Rankings;
using PinballApi.Models.WPPR.v2.Tournaments;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes competitive pinball data from the IFPA (International Flipper Pinball Association)
/// using the PinballApi NuGet package. Discovers player rankings, tournament data, and statistics.
/// Requires an IFPA API key (WPPRKey) — set via ScraperSettings.IfpaApiKey or IFPA_API_KEY env var.
/// </summary>
public sealed class IfpaScraper : ISourceScraper
{
    private readonly ScraperSettings _settings;
    private readonly ILogger<IfpaScraper> _logger;

    private const string IfpaBaseUrl = "https://www.ifpapinball.com";

    public string Name => "IFPA";

    public IfpaScraper(
        IOptions<ScraperSettings> settings,
        ILogger<IfpaScraper> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _settings.IfpaApiKey
                     ?? Environment.GetEnvironmentVariable("IFPA_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No IFPA API key configured. Set IfpaApiKey in settings or IFPA_API_KEY env var. " +
                               "Get a key at https://www.ifpapinball.com/api/");
            yield break;
        }

        _logger.LogInformation("Fetching IFPA competitive pinball data");

        var api = new PinballRankingApiV2(apiKey);
        var totalItems = 0;

        // Fetch top-ranked players
        await foreach (var item in ScrapeRankingsAsync(api, cancellationToken))
        {
            totalItems++;
            yield return item;
        }

        // Fetch recent tournaments
        await foreach (var item in ScrapeTournamentsAsync(api, cancellationToken))
        {
            totalItems++;
            yield return item;
        }

        _logger.LogInformation("IFPA: discovered {Count} competitive play items", totalItems);
    }

    private async IAsyncEnumerable<ScrapedItem> ScrapeRankingsAsync(
        PinballRankingApiV2 api,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching IFPA world rankings");

        var count = 0;

        // Fetch top 500 players (5 pages x 100)
        for (var startPos = 1; startPos <= 500 && !cancellationToken.IsCancellationRequested; startPos += 100)
        {
            WpprRanking? rankings;
            try
            {
                rankings = await api.GetWpprRanking(startPos, 100);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch IFPA rankings at position {Start}", startPos);
                break;
            }

            if (rankings?.Rankings is null or { Count: 0 }) break;

            foreach (var player in rankings.Rankings)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                var playerName = $"{player.FirstName} {player.LastName}".Trim();
                var profileUrl = $"{IfpaBaseUrl}/player.php?p={player.PlayerId}";

                count++;
                yield return new ScrapedItem
                {
                    Link = new DiscoveredLink
                    {
                        FileUrl = profileUrl,
                        LinkText = $"IFPA #{player.CurrentRank}: {playerName}",
                        DiscoveryContext = "IFPA World Rankings"
                    },
                    SourceType = SourceType.IfpaApi,
                    DiscoveryUrl = $"{IfpaBaseUrl}/rankings/overall.php",
                    DiscoveryContext = $"IFPA Ranked Player: {playerName} (#{player.CurrentRank})"
                };
            }

            await Task.Delay(300, cancellationToken);
        }

        _logger.LogInformation("IFPA rankings: discovered {Count} ranked players", count);
    }

    private async IAsyncEnumerable<ScrapedItem> ScrapeTournamentsAsync(
        PinballRankingApiV2 api,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching IFPA tournament data");

        // Search for recent tournaments by country
        var countries = new[] { "United States", "Canada", "United Kingdom", "Germany", "Australia" };
        var seenTournaments = new HashSet<int>();
        var count = 0;

        foreach (var country in countries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            TournamentSearch? results;
            try
            {
                var filter = new TournamentSearchFilter
                {
                    Country = country,
                    StartDate = DateTime.UtcNow.AddMonths(-6),
                    EndDate = DateTime.UtcNow.AddMonths(3)
                };
                results = await api.GetTournamentBySearch(filter);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search tournaments for {Country}", country);
                continue;
            }

            if (results?.Results is null) continue;

            var items = new List<ScrapedItem>();
            foreach (var tournament in results.Results)
            {
                if (!seenTournaments.Add(tournament.TournamentId)) continue;

                var tournamentUrl = $"{IfpaBaseUrl}/tournaments/view.php?t={tournament.TournamentId}";

                items.Add(new ScrapedItem
                {
                    Link = new DiscoveredLink
                    {
                        FileUrl = tournamentUrl,
                        LinkText = tournament.TournamentName ?? $"Tournament #{tournament.TournamentId}",
                        DiscoveryContext = $"IFPA Calendar ({country})"
                    },
                    SourceType = SourceType.IfpaApi,
                    DiscoveryUrl = $"{IfpaBaseUrl}/calendar/",
                    DiscoveryContext = $"IFPA Tournament: {tournament.TournamentName}"
                });
            }

            foreach (var item in items)
            {
                count++;
                yield return item;
            }

            await Task.Delay(300, cancellationToken);
        }

        _logger.LogInformation("IFPA tournaments: discovered {Count} tournaments", count);
    }
}
