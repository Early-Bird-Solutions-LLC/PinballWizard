using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Ifpa;

// Typed HttpClient for IFPA player / WPPR endpoints (ADR-0067).
// Auth is an `api_key` query parameter (OpenAPI 2.1 securitySchemes.API_KEY),
// not Bearer — appended per request. Not a PoliteScraperBase — partner/API
// surface with documented rate limits.
//
// OBS-03: the key lives only on the wire URL. Failure paths throw
// HttpRequestException messages that cite the key-free path so LogWarning(ex)
// in IfpaWpprTool never interpolates the secret.
public sealed class IfpaClient : IIfpaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IOptions<IfpaOptions> _options;
    private readonly ILogger<IfpaClient> _logger;

    public IfpaClient(
        HttpClient http,
        IOptions<IfpaOptions> options,
        ILogger<IfpaClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IfpaPlayerSearchHitWireDto>> SearchPlayersAsync(
        string name,
        string? country = null,
        string? stateProv = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var qs = new StringBuilder();
        qs.Append("name=").Append(Uri.EscapeDataString(name.Trim()));
        if (!string.IsNullOrWhiteSpace(country))
        {
            qs.Append("&country=").Append(Uri.EscapeDataString(country.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(stateProv))
        {
            qs.Append("&stateprov=").Append(Uri.EscapeDataString(stateProv.Trim()));
        }

        var publicPath = $"player/search?{qs}";
        using var response = await GetAsync(publicPath, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        EnsureSuccessWithoutLeakingKey(response, publicPath);
        var body = await response.Content
            .ReadFromJsonAsync<IfpaPlayerSearchResponseDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return [];
        }

        return ParseSearchResults(body.Results);
    }

    public async Task<IfpaPlayerWireDto?> GetPlayerAsync(
        int playerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playerId);

        var publicPath = $"player/{playerId}";
        using var response = await GetAsync(publicPath, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Ifpa: player 404 for playerId={PlayerId}", playerId);
            return null;
        }

        EnsureSuccessWithoutLeakingKey(response, publicPath);
        var body = await response.Content
            .ReadFromJsonAsync<IfpaPlayerEnvelopeDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return body?.Player;
    }

    private async Task<HttpResponseMessage> GetAsync(
        string publicPath,
        CancellationToken cancellationToken)
    {
        var url = WithApiKey(publicPath);
        return await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private string WithApiKey(string publicPath)
    {
        var key = _options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Ifpa:ApiKey is required but was empty when building an IFPA request.");
        }

        var separator = publicPath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{publicPath}{separator}api_key={Uri.EscapeDataString(key)}";
    }

    // Prefer an explicit throw over EnsureSuccessStatusCode so the exception
    // message cites the key-free path (query-param auth must not hit logs).
    private static void EnsureSuccessWithoutLeakingKey(
        HttpResponseMessage response,
        string publicPath)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException(
            $"IFPA request failed with {(int)response.StatusCode} {response.StatusCode} for {publicPath}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static List<IfpaPlayerSearchHitWireDto> ParseSearchResults(JsonElement results)
    {
        if (results.ValueKind == JsonValueKind.Array)
        {
            return results.Deserialize<List<IfpaPlayerSearchHitWireDto>>(JsonOptions) ?? [];
        }

        if (results.ValueKind == JsonValueKind.Object)
        {
            var single = results.Deserialize<IfpaPlayerSearchHitWireDto>(JsonOptions);
            return single is null ? [] : [single];
        }

        return [];
    }
}
