using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Tournaments;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// IMatchPlayTournamentProvider backed by Match Play Events API (ADR-0061).
// Enforces the handbook standings cadence (≥15s between fetches for the
// same in-progress tournament) via an in-memory cache. Completed
// tournament standings are cached for the process lifetime (results do
// not change once recorded).
internal sealed class MatchPlayTournamentProvider : IMatchPlayTournamentProvider
{
    private readonly IMatchPlayClient _client;
    private readonly MatchPlayOptions _options;
    private readonly ILogger<MatchPlayTournamentProvider> _logger;
    private readonly ConcurrentDictionary<int, CacheEntry> _standingsCache = new();

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

        if (_standingsCache.TryGetValue(tournamentId, out var cached)
            && !IsExpired(cached))
        {
            return cached.Value;
        }

        var tournament = await _client
            .GetTournamentAsync(tournamentId, includePlayers: true, cancellationToken)
            .ConfigureAwait(false);

        if (tournament is null)
        {
            _logger.LogDebug(
                "MatchPlay: tournament {TournamentId} not found",
                tournamentId);
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
            string.IsNullOrWhiteSpace(tournament.Name) ? $"Tournament {tournamentId}" : tournament.Name!,
            string.IsNullOrWhiteSpace(tournament.Status) ? "unknown" : tournament.Status!,
            tournament.StartUtc,
            PublicUrl(tournament.TournamentId),
            rows,
            DateTimeOffset.UtcNow);

        var ttl = IsInProgress(result.Status)
            ? TimeSpan.FromSeconds(Math.Max(15, _options.StandingsMinIntervalSeconds))
            : TimeSpan.FromHours(24);

        _standingsCache[tournamentId] = new CacheEntry(result, DateTimeOffset.UtcNow.Add(ttl));
        return result;
    }

    private static bool IsExpired(CacheEntry entry) =>
        DateTimeOffset.UtcNow >= entry.ExpiresAtUtc;

    private static bool IsInProgress(string status) =>
        status.Equals("started", StringComparison.OrdinalIgnoreCase)
        || status.Equals("planned", StringComparison.OrdinalIgnoreCase);

    private static MatchPlayTournamentSummary MapSummary(MatchPlayTournamentWireDto h) =>
        new(
            h.TournamentId,
            h.Name!,
            string.IsNullOrWhiteSpace(h.Status) ? "unknown" : h.Status!,
            h.StartUtc,
            h.Type,
            PublicUrl(h.TournamentId));

    private static string PublicUrl(int tournamentId) =>
        $"https://app.matchplay.events/tournaments/{tournamentId}";

    private sealed record CacheEntry(MatchPlayTournamentStandings Value, DateTimeOffset ExpiresAtUtc);
}
