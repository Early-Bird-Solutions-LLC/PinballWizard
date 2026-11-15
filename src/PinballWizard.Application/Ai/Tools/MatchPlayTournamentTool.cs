using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Tournaments;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tools for live Match Play Events data (ADR-0061 / ADR-0062).
// Wizard-only — ToolTraceCitationExtractor observes Wizard FunctionResultContent.
//
// IMatchPlayTournamentProvider is optional (nullable DI). When absent — because
// MatchPlay/Opdb API token is not configured — tools return null immediately.
//
// CancellationToken defaults are load-bearing for AIFunctionFactory schemas
// (same rationale as MarketValueTool / SearchCorpusTool).
//
// Fail closed on unexpected faults — return null so the Wizard can degrade
// honestly rather than aborting the turn. Broad catch mirrors MarketValueTool.
public sealed class MatchPlayTournamentTool
{
    internal const string SearchToolTag = "searchMatchPlayTournaments";
    internal const string StandingsToolTag = "getMatchPlayTournamentStandings";
    internal const string DetailToolTag = "getMatchPlayTournamentDetail";
    internal const string ArenasToolTag = "getMatchPlayTournamentArenas";
    internal const string FloorToolTag = "getMatchPlayTournamentFloor";
    internal const string PlayersToolTag = "findMatchPlayTournamentPlayers";

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

    [Description("Search Match Play Events tournaments by name (e.g. 'INDISC', 'Pinburgh'). Returns up to 10 candidates with tournamentId, status (planned/started/completed), and public URL. Use before other Match Play tools when the user named an event rather than a numeric tournament id. Returns null when Match Play is not configured or the search yields nothing.")]
    public async Task<IReadOnlyList<MatchPlayTournamentHitDto>?> SearchMatchPlayTournamentsAsync(
        [Description("Tournament or event name fragment to search for.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(SearchToolTag) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return await RunAsync(
            SearchToolTag,
            async () =>
            {
                var hits = await _provider!
                    .SearchTournamentsAsync(query.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return null;
                }

                return (IReadOnlyList<MatchPlayTournamentHitDto>?)hits
                    .Take(10)
                    .Select(h => new MatchPlayTournamentHitDto(
                        h.TournamentId,
                        h.Name,
                        h.Status,
                        h.StartUtc?.ToString("O"),
                        h.Type,
                        h.PublicUrl))
                    .ToList();
            },
            ex => FailNull<IReadOnlyList<MatchPlayTournamentHitDto>>(ex, SearchToolTag, $"query={query}"),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Get live Match Play Events standings for a tournament by numeric tournamentId. Returns ranked rows with player names, points, and games played, plus a public Match Play URL for attribution. Prefer searching first when the user gave a name. Returns null when Match Play is not configured, the id is unknown, or the API fails. Never invent standings.")]
    public async Task<MatchPlayTournamentStandingsDto?> GetMatchPlayTournamentStandingsAsync(
        [Description("Match Play tournament id (integer from searchMatchPlayTournaments or a known event).")] int tournamentId,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(StandingsToolTag) || !ValidId(tournamentId, StandingsToolTag))
        {
            return null;
        }

        return await RunAsync(
            StandingsToolTag,
            async () =>
            {
                var result = await _provider!
                    .GetStandingsAsync(tournamentId, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                return new MatchPlayTournamentStandingsDto(
                    result.TournamentId,
                    result.Name,
                    result.Status,
                    result.StartUtc?.ToString("O"),
                    result.PublicUrl,
                    Attribution(result.PublicUrl),
                    Standings: result.Standings
                        .Select(s => new MatchPlayStandingRowDto(
                            s.Position,
                            s.PlayerName,
                            s.PlayerId,
                            s.Points,
                            s.GamesPlayed,
                            s.IfpaId))
                        .ToList());
            },
            ex => FailNull<MatchPlayTournamentStandingsDto>(ex, StandingsToolTag, $"tournamentId={tournamentId}"),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Get Match Play tournament detail: location, timezone, series, description, and linked tournaments (quals/playoffs). Use for where/when/series questions. Returns null when unavailable.")]
    public async Task<MatchPlayTournamentDetailDto?> GetMatchPlayTournamentDetailAsync(
        [Description("Match Play tournament id.")] int tournamentId,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(DetailToolTag) || !ValidId(tournamentId, DetailToolTag))
        {
            return null;
        }

        return await RunAsync(
            DetailToolTag,
            async () =>
            {
                var result = await _provider!
                    .GetTournamentDetailAsync(tournamentId, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                return new MatchPlayTournamentDetailDto(
                    result.TournamentId,
                    result.Name,
                    result.Status,
                    result.Type,
                    result.StartUtc?.ToString("O"),
                    result.EndUtc?.ToString("O"),
                    result.Timezone,
                    result.Description,
                    result.Location is null
                        ? null
                        : new MatchPlayLocationDto(
                            result.Location.LocationId,
                            result.Location.Name,
                            result.Location.City,
                            result.Location.State,
                            result.Location.Country),
                    result.Series is null
                        ? null
                        : new MatchPlaySeriesDto(result.Series.SeriesId, result.Series.Name),
                    result.LinkedTournaments
                        .Select(l => new MatchPlayLinkedTournamentDto(
                            l.TournamentId, l.Name, l.Status, l.Type, l.PublicUrl))
                        .ToList(),
                    result.PublicUrl,
                    Attribution(result.PublicUrl));
            },
            ex => FailNull<MatchPlayTournamentDetailDto>(ex, DetailToolTag, $"tournamentId={tournamentId}"),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("List machines (arenas) on the Match Play tournament floor with optional OPDB ids. Use for 'what machines are they playing' / lineup questions. Returns null when unavailable.")]
    public async Task<MatchPlayTournamentArenasDto?> GetMatchPlayTournamentArenasAsync(
        [Description("Match Play tournament id.")] int tournamentId,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(ArenasToolTag) || !ValidId(tournamentId, ArenasToolTag))
        {
            return null;
        }

        return await RunAsync(
            ArenasToolTag,
            async () =>
            {
                var result = await _provider!
                    .GetArenasAsync(tournamentId, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                return new MatchPlayTournamentArenasDto(
                    result.TournamentId,
                    result.Name,
                    result.PublicUrl,
                    Attribution(result.PublicUrl),
                    result.Arenas
                        .Select(a => new MatchPlayArenaDto(a.ArenaId, a.Name, a.Status, a.OpdbId))
                        .ToList());
            },
            ex => FailNull<MatchPlayTournamentArenasDto>(ex, ArenasToolTag, $"tournamentId={tournamentId}"),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Get Match Play tournament rounds plus in-progress (or filtered) multi-player games — who is playing which machine right now. Optional gameStatus: started (default), completed. Optional playerId to filter games for one player. Never invent games.")]
    public async Task<MatchPlayTournamentFloorDto?> GetMatchPlayTournamentFloorAsync(
        [Description("Match Play tournament id.")] int tournamentId,
        [Description("Game status filter: started (default) or completed.")] string? gameStatus = null,
        [Description("Optional Match Play player id to filter games.")] int? playerId = null,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(FloorToolTag) || !ValidId(tournamentId, FloorToolTag))
        {
            return null;
        }

        return await RunAsync(
            FloorToolTag,
            async () =>
            {
                var result = await _provider!
                    .GetRoundsAndGamesAsync(tournamentId, gameStatus, playerId, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                return new MatchPlayTournamentFloorDto(
                    result.TournamentId,
                    result.Name,
                    result.PublicUrl,
                    Attribution(result.PublicUrl),
                    result.Rounds
                        .Select(r => new MatchPlayRoundDto(
                            r.RoundId, r.Index, r.Name, r.Status, r.ArenaId))
                        .ToList(),
                    result.Games
                        .Select(g => new MatchPlayGameDto(
                            g.GameId,
                            g.RoundId,
                            g.Status,
                            g.Bye,
                            g.ArenaId,
                            g.ArenaName,
                            g.ArenaOpdbId,
                            g.Players
                                .Select(p => new MatchPlayGamePlayerDto(
                                    p.PlayerId, p.PlayerName, p.ResultPosition, p.ResultPoints))
                                .ToList()))
                        .ToList());
            },
            ex => FailNull<MatchPlayTournamentFloorDto>(ex, FloorToolTag, $"tournamentId={tournamentId}"),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Find players entered in a Match Play tournament by name fragment or IFPA id. Returns Match Play playerId (and IFPA id when known) for follow-up standings/floor calls. Returns null when unavailable or no matches.")]
    public async Task<MatchPlayPlayerLookupDto?> FindMatchPlayTournamentPlayersAsync(
        [Description("Match Play tournament id.")] int tournamentId,
        [Description("Player name fragment or numeric IFPA id.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(PlayersToolTag)
            || !ValidId(tournamentId, PlayersToolTag)
            || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return await RunAsync(
            PlayersToolTag,
            async () =>
            {
                var result = await _provider!
                    .FindPlayersAsync(tournamentId, query.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                if (result is null || result.Players.Count == 0)
                {
                    return null;
                }

                return new MatchPlayPlayerLookupDto(
                    result.TournamentId,
                    result.TournamentName,
                    result.PublicUrl,
                    Attribution(result.PublicUrl),
                    result.Query,
                    result.Players
                        .Select(p => new MatchPlayPlayerDto(p.PlayerId, p.Name, p.IfpaId))
                        .ToList());
            },
            ex => FailNull<MatchPlayPlayerLookupDto>(
                ex, PlayersToolTag, $"tournamentId={tournamentId} query={query}"),
            cancellationToken).ConfigureAwait(false);
    }

    private bool EnsureProvider(string toolTag)
    {
        if (_provider is not null)
        {
            return true;
        }

        _logger.LogDebug(
            "MatchPlayTournamentTool: provider not registered — returning null from {Tool}",
            toolTag);
        return false;
    }

    private bool ValidId(int tournamentId, string toolTag)
    {
        if (tournamentId > 0)
        {
            return true;
        }

        _logger.LogDebug(
            "MatchPlayTournamentTool: invalid tournamentId={TournamentId} for {Tool}",
            tournamentId,
            toolTag);
        return false;
    }

    private async Task<T?> RunAsync<T>(
        string toolTag,
        Func<Task<T?>> action,
        Func<Exception, T?> onFault,
        CancellationToken _)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Propagate caller cancel and HttpClient TaskCanceledException —
            // same posture as MarketValueTool. Polly TimeoutRejectedException /
            // BrokenCircuitException fall through to Exception below.
            throw;
        }
        catch (Exception ex)
        {
            return onFault(ex);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", toolTag));
        }
    }

    private T? FailNull<T>(Exception ex, string toolTag, string context)
        where T : class
    {
        PinballWizardTelemetry.AiToolErrors.Add(
            1,
            new KeyValuePair<string, object?>("tool", toolTag));
        _logger.LogWarning(ex, "MatchPlayTournamentTool: {Tool} failed {Context}", toolTag, context);
        return null;
    }

    private static string Attribution(string publicUrl) =>
        $"Match Play Events — {publicUrl}";
}
