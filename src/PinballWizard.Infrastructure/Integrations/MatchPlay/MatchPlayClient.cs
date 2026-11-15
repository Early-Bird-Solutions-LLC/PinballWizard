using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Typed HttpClient for Match Play tournament endpoints (ADR-0061).
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
        bool includePlayers,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tournamentId);

        var url = includePlayers
            ? $"api/tournaments/{tournamentId}?includePlayers=true"
            : $"api/tournaments/{tournamentId}";

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
}
