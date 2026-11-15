using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Tournaments;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// IMatchPlayTournamentProvider backed by Match Play Events API
// (ADR-0061 / ADR-0062). Enforces the handbook standings cadence
// (≥15s between fetches for the same in-progress tournament) via an
// in-memory cache. Completed tournament standings are cached for 24h.
// Floor (rounds/games) and arenas use a shorter TTL while in progress.
internal sealed class MatchPlayTournamentProvider : IMatchPlayTournamentProvider
{
    private readonly IMatchPlayClient _client;
    private readonly MatchPlayOptions _options;
    private readonly ILogger<MatchPlayTournamentProvider> _logger;
    private readonly ConcurrentDictionary<int, CacheEntry<MatchPlayTournamentStandings>> _standingsCache = new();
    private readonly ConcurrentDictionary<int, CacheEntry<MatchPlayTournamentFloor>> _floorCache = new();
    private readonly ConcurrentDictionary<int, CacheEntry<MatchPlayTournamentArenas>> _arenasCache = new();
    private readonly ConcurrentDictionary<int, CacheEntry<MatchPlayTournamentDetail>> _detailCache = new();

    public MatchPlayTournamentProvider(
        IMatchPlayClient client,
        IOptions<MatchPlayOptions> options,
        ILogger<MatchPlayTournamentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MatchPlayTournamentSummary>> SearchTournamentsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var hits = await _client.SearchTournamentsAsync(query, cancellationToken).ConfigureAwait(false);
        return hits
            .Where(h => h.TournamentId > 0 && !string.IsNullOrWhiteSpace(h.Name))
            .Select(MapSummary)
            .ToList();
    }

    public async Task<MatchPlayTournamentStandings?> GetStandingsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        if (_standingsCache.TryGetValue(tournamentId, out var cached) && !IsExpired(cached))
        {
            return cached.Value;
        }

        var tournament = await _client
            .GetTournamentAsync(tournamentId, MatchPlayTournamentInclude.Players, cancellationToken)
            .ConfigureAwait(false);

        if (tournament is null)
        {
            _logger.LogDebug("MatchPlay: tournament {TournamentId} not found", tournamentId);
            return null;
        }

        var wireStandings = await _client
            .GetStandingsAsync(tournamentId, cancellationToken)
            .ConfigureAwait(false);

        var players = (tournament.Players ?? [])
            .ToDictionary(p => p.PlayerId, p => p);

        var rows = wireStandings
            .OrderBy(s => s.Position)
            .ThenBy(s => s.PlayerId)
            .Select(s =>
            {
                players.TryGetValue(s.PlayerId, out var player);
                var name = string.IsNullOrWhiteSpace(player?.Name)
                    ? $"Player {s.PlayerId}"
                    : player!.Name!;
                return new MatchPlayStandingRow(
                    s.Position,
                    s.PlayerId,
                    name,
                    s.Points,
                    s.GamesPlayed,
                    player?.IfpaId);
            })
            .ToList();

        var result = new MatchPlayTournamentStandings(
            tournament.TournamentId,
            DisplayName(tournament),
            StatusOrUnknown(tournament.Status),
            tournament.StartUtc,
            PublicUrl(tournament.TournamentId),
            rows,
            DateTimeOffset.UtcNow);

        Cache(_standingsCache, tournamentId, result, TtlForStatus(result.Status));
        return result;
    }

    public async Task<MatchPlayTournamentDetail?> GetTournamentDetailAsync(
        int tournamentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        if (_detailCache.TryGetValue(tournamentId, out var cached) && !IsExpired(cached))
        {
            return cached.Value;
        }

        var include = MatchPlayTournamentInclude.Location
            | MatchPlayTournamentInclude.Series
            | MatchPlayTournamentInclude.LinkedTournaments
            | MatchPlayTournamentInclude.Event;

        var tournament = await _client
            .GetTournamentAsync(tournamentId, include, cancellationToken)
            .ConfigureAwait(false);

        if (tournament is null)
        {
            return null;
        }

        MatchPlayLocationInfo? location = null;
        if (tournament.Location is { LocationId: > 0 } loc)
        {
            location = new MatchPlayLocationInfo(
                loc.LocationId, loc.Name, loc.City, loc.State, loc.Country);
        }

        MatchPlaySeriesInfo? series = null;
        if (tournament.Series is { SeriesId: > 0 } s)
        {
            series = new MatchPlaySeriesInfo(s.SeriesId, s.Name);
        }
        else if (tournament.SeriesId is > 0)
        {
            series = new MatchPlaySeriesInfo(tournament.SeriesId.Value, null);
        }

        var linked = (tournament.LinkedTournaments ?? [])
            .Where(l => l.TournamentId > 0 && !string.IsNullOrWhiteSpace(l.Name))
            .Select(l => new MatchPlayLinkedTournamentInfo(
                l.TournamentId,
                l.Name!,
                StatusOrUnknown(l.Status),
                l.Type,
                PublicUrl(l.TournamentId)))
            .ToList();

        if (linked.Count == 0 && tournament.LinkedTournamentId is > 0)
        {
            var linkedId = tournament.LinkedTournamentId.Value;
            linked.Add(new MatchPlayLinkedTournamentInfo(
                linkedId,
                $"Tournament {linkedId}",
                "unknown",
                null,
                PublicUrl(linkedId)));
        }

        var result = new MatchPlayTournamentDetail(
            tournament.TournamentId,
            DisplayName(tournament),
            StatusOrUnknown(tournament.Status),
            tournament.Type,
            tournament.StartUtc,
            tournament.EndUtc,
            tournament.Timezone,
            tournament.Description,
            location,
            series,
            linked,
            PublicUrl(tournament.TournamentId),
            DateTimeOffset.UtcNow);

        Cache(_detailCache, tournamentId, result, TtlForStatus(result.Status));
        return result;
    }

    public async Task<MatchPlayTournamentArenas?> GetArenasAsync(
        int tournamentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        if (_arenasCache.TryGetValue(tournamentId, out var cached) && !IsExpired(cached))
        {
            return cached.Value;
        }

        var tournament = await _client
            .GetTournamentAsync(tournamentId, MatchPlayTournamentInclude.Arenas, cancellationToken)
            .ConfigureAwait(false);

        if (tournament is null)
        {
            return null;
        }

        var arenas = (tournament.Arenas ?? [])
            .Where(a => a.ArenaId > 0 && !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new MatchPlayArenaInfo(
                a.ArenaId,
                a.Name!,
                a.Status,
                string.IsNullOrWhiteSpace(a.OpdbId) ? null : a.OpdbId))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new MatchPlayTournamentArenas(
            tournament.TournamentId,
            DisplayName(tournament),
            PublicUrl(tournament.TournamentId),
            arenas,
            DateTimeOffset.UtcNow);

        Cache(_arenasCache, tournamentId, result, TtlForStatus(StatusOrUnknown(tournament.Status)));
        return result;
    }

    public async Task<MatchPlayTournamentFloor?> GetRoundsAndGamesAsync(
        int tournamentId,
        string? gameStatus = null,
        int? playerId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        // Cache only unfiltered "what's happening now" (started games) fetches.
        var cacheable = string.IsNullOrWhiteSpace(gameStatus)
            && playerId is null or <= 0;
        if (cacheable
            && _floorCache.TryGetValue(tournamentId, out var cached)
            && !IsExpired(cached))
        {
            return cached.Value;
        }

        var tournament = await _client
            .GetTournamentAsync(
                tournamentId,
                MatchPlayTournamentInclude.Players | MatchPlayTournamentInclude.Arenas,
                cancellationToken)
            .ConfigureAwait(false);

        if (tournament is null)
        {
            return null;
        }

        var players = (tournament.Players ?? [])
            .ToDictionary(p => p.PlayerId, p => p);
        var arenas = (tournament.Arenas ?? [])
            .ToDictionary(a => a.ArenaId, a => a);

        var roundsTask = _client.GetRoundsAsync(tournamentId, cancellationToken);
        // Default to in-progress games for "what's on now"; callers can pass
        // "completed" or omit via explicit status when needed.
        var statusFilter = string.IsNullOrWhiteSpace(gameStatus) ? "started" : gameStatus.Trim();
        var gamesTask = _client.GetTournamentGamesAsync(
            tournamentId,
            status: statusFilter,
            playerId: playerId is > 0 ? playerId : null,
            cancellationToken: cancellationToken);

        await Task.WhenAll(roundsTask, gamesTask).ConfigureAwait(false);

        var rounds = (await roundsTask.ConfigureAwait(false))
            .OrderBy(r => r.Index)
            .Select(r => new MatchPlayRoundInfo(
                r.RoundId,
                r.Index,
                string.IsNullOrWhiteSpace(r.Name) ? $"Round {r.Index + 1}" : r.Name!,
                StatusOrUnknown(r.Status),
                r.ArenaId))
            .ToList();

        var games = (await gamesTask.ConfigureAwait(false))
            .Select(g => MapGame(g, players, arenas))
            .ToList();

        var result = new MatchPlayTournamentFloor(
            tournament.TournamentId,
            DisplayName(tournament),
            PublicUrl(tournament.TournamentId),
            rounds,
            games,
            DateTimeOffset.UtcNow);

        if (cacheable)
        {
            // Floor moves faster than standings — 15s min while live.
            Cache(_floorCache, tournamentId, result, TtlForStatus(StatusOrUnknown(tournament.Status)));
        }

        return result;
    }

    public async Task<MatchPlayPlayerLookup?> FindPlayersAsync(
        int tournamentId,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var tournament = await _client
            .GetTournamentAsync(tournamentId, MatchPlayTournamentInclude.Players, cancellationToken)
            .ConfigureAwait(false);

        if (tournament is null)
        {
            return null;
        }

        var needle = query.Trim();
        var ifpaNeedle = int.TryParse(needle, out var ifpaId) ? ifpaId : (int?)null;

        var hits = (tournament.Players ?? [])
            .Where(p => p.PlayerId > 0 && !string.IsNullOrWhiteSpace(p.Name))
            .Where(p =>
                (ifpaNeedle is not null && p.IfpaId == ifpaNeedle)
                || p.Name!.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (ifpaNeedle is null
                    && p.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        .Equals(needle, StringComparison.Ordinal)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(p => new MatchPlayPlayerInfo(p.PlayerId, p.Name!, p.IfpaId))
            .ToList();

        return new MatchPlayPlayerLookup(
            tournament.TournamentId,
            DisplayName(tournament),
            PublicUrl(tournament.TournamentId),
            needle,
            hits,
            DateTimeOffset.UtcNow);
    }

    private static MatchPlayGameInfo MapGame(
        MatchPlayGameWireDto g,
        Dictionary<int, MatchPlayPlayerWireDto> players,
        Dictionary<int, MatchPlayArenaWireDto> arenas)
    {
        string? arenaName = g.Arena?.Name;
        string? arenaOpdb = g.Arena?.OpdbId;
        var arenaId = g.ArenaId ?? g.Arena?.ArenaId;
        if (arenaId is > 0
            && arenas.TryGetValue(arenaId.Value, out var arena)
            && string.IsNullOrWhiteSpace(arenaName))
        {
            arenaName = arena.Name;
            arenaOpdb = string.IsNullOrWhiteSpace(arenaOpdb) ? arena.OpdbId : arenaOpdb;
        }

        var slots = new List<MatchPlayGamePlayerSlot>();
        var playerIds = g.PlayerIds ?? [];
        for (var i = 0; i < playerIds.Count; i++)
        {
            var id = playerIds[i];
            players.TryGetValue(id, out var player);
            var name = string.IsNullOrWhiteSpace(player?.Name) ? $"Player {id}" : player!.Name!;
            int? pos = g.ResultPositions is { Count: > 0 } && i < g.ResultPositions.Count
                ? g.ResultPositions[i]
                : null;
            string? pts = g.ResultPoints is { Count: > 0 } && i < g.ResultPoints.Count
                ? g.ResultPoints[i]
                : null;
            slots.Add(new MatchPlayGamePlayerSlot(id, name, pos, pts));
        }

        return new MatchPlayGameInfo(
            g.GameId,
            g.RoundId,
            StatusOrUnknown(g.Status),
            g.Bye,
            arenaId,
            arenaName,
            string.IsNullOrWhiteSpace(arenaOpdb) ? null : arenaOpdb,
            slots);
    }

    private TimeSpan TtlForStatus(string status)
    {
        if (IsInProgress(status))
        {
            return TimeSpan.FromSeconds(Math.Max(15, _options.StandingsMinIntervalSeconds));
        }

        return TimeSpan.FromHours(24);
    }

    private static void Cache<T>(
        ConcurrentDictionary<int, CacheEntry<T>> cache,
        int tournamentId,
        T value,
        TimeSpan ttl) =>
        cache[tournamentId] = new CacheEntry<T>(value, DateTimeOffset.UtcNow.Add(ttl));

    private static bool IsExpired<T>(CacheEntry<T> entry) =>
        DateTimeOffset.UtcNow >= entry.ExpiresAtUtc;

    private static bool IsInProgress(string status) =>
        status.Equals("started", StringComparison.OrdinalIgnoreCase)
        || status.Equals("planned", StringComparison.OrdinalIgnoreCase);

    private static MatchPlayTournamentSummary MapSummary(MatchPlayTournamentWireDto h) =>
        new(
            h.TournamentId,
            h.Name!,
            StatusOrUnknown(h.Status),
            h.StartUtc,
            h.Type,
            PublicUrl(h.TournamentId));

    private static string DisplayName(MatchPlayTournamentWireDto t) =>
        string.IsNullOrWhiteSpace(t.Name) ? $"Tournament {t.TournamentId}" : t.Name!;

    private static string StatusOrUnknown(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "unknown" : status;

    private static string PublicUrl(int tournamentId) =>
        $"https://app.matchplay.events/tournaments/{tournamentId}";

    private sealed record CacheEntry<T>(T Value, DateTimeOffset ExpiresAtUtc);
}
