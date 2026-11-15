using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Tournaments;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tools for live Match Play Events data (ADR-0061).
// Wizard-only — ToolTraceCitationExtractor observes Wizard FunctionResultContent.
//
// IMatchPlayTournamentProvider is optional (nullable DI). When absent — because
// MatchPlay/Opdb API token is not configured — tools return null immediately.
//
// CancellationToken defaults are load-bearing for AIFunctionFactory schemas
// (same rationale as MarketValueTool / SearchCorpusTool).
//
// Fail closed on unexpected faults — return null so the Wizard can degrade
// honestly rather than aborting the turn. Broad catch mirrors MarketValueTool /
// KineticistGuideLinkTool: AddStandardResilienceHandler surfaces Polly
// TimeoutRejectedException / BrokenCircuitException, and mapping can throw
// ArgumentException — none of which should kill the Wizard turn. Caller
// cancellation (OperationCanceledException) still propagates.
public sealed class MatchPlayTournamentTool
{
    internal const string SearchToolTag = "searchMatchPlayTournaments";
    internal const string StandingsToolTag = "getMatchPlayTournamentStandings";

    private readonly IMatchPlayTournamentProvider? _provider;
    private readonly ILogger<MatchPlayTournamentTool> _logger;

    public MatchPlayTournamentTool(
        ILogger<MatchPlayTournamentTool> logger,
        IMatchPlayTournamentProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _provider = provider;
    }

    [Description("Search Match Play Events tournaments by name (e.g. 'INDISC', 'Pinburgh'). Returns up to 10 candidates with tournamentId, status (planned/started/completed), and public URL. Use before getMatchPlayTournamentStandings when the user named an event rather than a numeric tournament id. Returns null when Match Play is not configured or the search yields nothing.")]
    public async Task<IReadOnlyList<MatchPlayTournamentHitDto>?> SearchMatchPlayTournamentsAsync(
        [Description("Tournament or event name fragment to search for.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (_provider is null)
        {
            _logger.LogDebug(
                "MatchPlayTournamentTool: IMatchPlayTournamentProvider not registered — returning null from search.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogDebug("MatchPlayTournamentTool: empty search query — returning null.");
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var hits = await _provider
                .SearchTournamentsAsync(query.Trim(), cancellationToken)
                .ConfigureAwait(false);

            if (hits.Count == 0)
            {
                _logger.LogDebug("MatchPlayTournamentTool: no tournaments for query={Query}", query);
                return null;
            }

            return hits
                .Take(10)
                .Select(h => new MatchPlayTournamentHitDto(
                    h.TournamentId,
                    h.Name,
                    h.Status,
                    h.StartUtc?.ToString("O"),
                    h.Type,
                    h.PublicUrl))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broad catch intentional — Polly resilience + mapping faults must
            // fail closed (null) rather than abort the Wizard turn. Same posture
            // as MarketValueTool.
            return FailSearch(ex, query);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", SearchToolTag));
        }
    }

    [Description("Get live Match Play Events standings for a tournament by numeric tournamentId. Returns ranked rows with player names, points, and games played, plus a public Match Play URL for attribution. Prefer searching first when the user gave a name. Returns null when Match Play is not configured, the id is unknown, or the API fails. Never invent standings.")]
    public async Task<MatchPlayTournamentStandingsDto?> GetMatchPlayTournamentStandingsAsync(
        [Description("Match Play tournament id (integer from searchMatchPlayTournaments or a known event).")] int tournamentId,
        CancellationToken cancellationToken = default)
    {
        if (_provider is null)
        {
            _logger.LogDebug(
                "MatchPlayTournamentTool: IMatchPlayTournamentProvider not registered — returning null from standings.");
            return null;
        }

        if (tournamentId <= 0)
        {
            _logger.LogDebug(
                "MatchPlayTournamentTool: invalid tournamentId={TournamentId} — returning null.",
                tournamentId);
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _provider
                .GetStandingsAsync(tournamentId, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                _logger.LogDebug(
                    "MatchPlayTournamentTool: no standings for tournamentId={TournamentId}",
                    tournamentId);
                return null;
            }

            return new MatchPlayTournamentStandingsDto(
                result.TournamentId,
                result.Name,
                result.Status,
                result.StartUtc?.ToString("O"),
                result.PublicUrl,
                Attribution: $"Match Play Events — {result.PublicUrl}",
                Standings: result.Standings
                    .Select(s => new MatchPlayStandingRowDto(
                        s.Position,
                        s.PlayerName,
                        s.PlayerId,
                        s.Points,
                        s.GamesPlayed,
                        s.IfpaId))
                    .ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broad catch intentional — Polly resilience + mapping faults must
            // fail closed (null) rather than abort the Wizard turn. Same posture
            // as MarketValueTool.
            return FailStandings(ex, tournamentId);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", StandingsToolTag));
        }
    }

    private IReadOnlyList<MatchPlayTournamentHitDto>? FailSearch(Exception ex, string query)
    {
        PinballWizardTelemetry.AiToolErrors.Add(
            1,
            new KeyValuePair<string, object?>("tool", SearchToolTag));
        _logger.LogWarning(ex, "MatchPlayTournamentTool: search failed query={Query}", query);
        return null;
    }

    private MatchPlayTournamentStandingsDto? FailStandings(Exception ex, int tournamentId)
    {
        PinballWizardTelemetry.AiToolErrors.Add(
            1,
            new KeyValuePair<string, object?>("tool", StandingsToolTag));
        _logger.LogWarning(
            ex,
            "MatchPlayTournamentTool: standings failed tournamentId={TournamentId}",
            tournamentId);
        return null;
    }
}
