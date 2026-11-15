using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Typed HttpClient for Match Play tournament endpoints (ADR-0061 / ADR-0062).
// Auth header is applied by AddMatchPlayIntegration. Not a PoliteScraperBase —
// partner/API surface with documented rate limits; standings cadence is
// enforced in MatchPlayTournamentProvider.
public sealed class MatchPlayClient : IMatchPlayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<MatchPlayClient> _logger;

    public MatchPlayClient(HttpClient http, ILogger<MatchPlayClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MatchPlayTournamentWireDto>> SearchTournamentsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var url = $"api/search?type=tournaments&query={Uri.EscapeDataString(query.Trim())}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<MatchPlaySearchResponseDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return body?.Data ?? [];
    }

    public async Task<MatchPlayTournamentWireDto?> GetTournamentAsync(
        int tournamentId,
        MatchPlayTournamentInclude include,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        var url = BuildTournamentUrl(tournamentId, include);
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<MatchPlayTournamentEnvelopeDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return body?.Data;
    }

    public async Task<IReadOnlyList<MatchPlayStandingWireDto>> GetStandingsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        var url = $"api/tournaments/{tournamentId}/standings";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("MatchPlay: standings 404 for tournamentId={TournamentId}", tournamentId);
            return [];
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<List<MatchPlayStandingWireDto>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return body ?? [];
    }

    public async Task<IReadOnlyList<MatchPlayRoundWireDto>> GetRoundsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        var url = $"api/tournaments/{tournamentId}/rounds";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<MatchPlayListEnvelopeDto<MatchPlayRoundWireDto>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return body?.Data ?? [];
    }

    public async Task<IReadOnlyList<MatchPlayGameWireDto>> GetTournamentGamesAsync(
        int tournamentId,
        string? status = null,
        int? playerId = null,
        int? arenaId = null,
        int? roundId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        var qs = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(status))
        {
            qs.Append("&status=").Append(Uri.EscapeDataString(status.Trim()));
        }

        if (playerId is > 0)
        {
            qs.Append("&player=").Append(playerId.Value);
        }

        if (arenaId is > 0)
        {
            qs.Append("&arena=").Append(arenaId.Value);
        }

        if (roundId is > 0)
        {
            qs.Append("&round=").Append(roundId.Value);
        }

        // Prefer tournament-scoped games endpoint (includes result data).
        var url = $"api/tournaments/{tournamentId}/games?limit=50{qs}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<MatchPlayListEnvelopeDto<MatchPlayGameWireDto>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return body?.Data ?? [];
    }

    private static string BuildTournamentUrl(int tournamentId, MatchPlayTournamentInclude include)
    {
        if (include == MatchPlayTournamentInclude.None)
        {
            return $"api/tournaments/{tournamentId}";
        }

        var qs = new StringBuilder();
        void Add(MatchPlayTournamentInclude flag, string name)
        {
            if ((include & flag) == 0)
            {
                return;
            }

            if (qs.Length > 0)
            {
                qs.Append('&');
            }

            qs.Append(name).Append("=true");
        }

        Add(MatchPlayTournamentInclude.Players, "includePlayers");
        Add(MatchPlayTournamentInclude.Arenas, "includeArenas");
        Add(MatchPlayTournamentInclude.Location, "includeLocation");
        Add(MatchPlayTournamentInclude.Series, "includeSeries");
        Add(MatchPlayTournamentInclude.LinkedTournaments, "includeLinkedTournaments");
        Add(MatchPlayTournamentInclude.Event, "includeEvent");

        return $"api/tournaments/{tournamentId}?{qs}";
    }
}
