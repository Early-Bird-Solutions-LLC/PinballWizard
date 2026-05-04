using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Typed HTTP client for the OPDB (Open Pinball Database) REST API.
/// Extends <see cref="PoliteScraperBase"/> so requests to opdb.org
/// flow through the politeness gate with the same per-origin throttle
/// + 429-backoff invariants as every other source.
/// </summary>
/// <remarks>
/// Authenticates via bearer token from <see cref="OpdbOptions.ApiToken"/>.
/// Public endpoints are also reachable without a token; for the v1
/// sync we always include the token because the endpoints we hit
/// require it.
/// </remarks>
public sealed class OpdbClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly OpdbOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Initializes a new <see cref="OpdbClient"/>.</summary>
    public OpdbClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<OpdbOptions> opdbOptions,
        ILogger<OpdbClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(opdbOptions);
        _httpClient = httpClient;
        _options = opdbOptions.Value;
    }

    /// <summary>
    /// Streams the full OPDB machine catalog. Each yielded record is a
    /// parsed <see cref="OpdbMachineDto"/>; the caller decides whether
    /// to filter, map, or upsert.
    /// </summary>
    /// <remarks>
    /// OPDB exposes the complete machine catalog via a single bulk
    /// endpoint, <c>/api/export</c>, which returns one large JSON
    /// array of every machine. There is no paginated <c>/api/machines</c>
    /// endpoint (404 against the live API) — the original assumption
    /// in PR <c>d9face6</c> was incorrect and surfaced by the Phase 2
    /// Item 4 operational hand-off; see <c>docs/decision-log.md</c>
    /// DL-0003. The response is consumed via
    /// <see cref="JsonSerializer.DeserializeAsyncEnumerable{T}(System.IO.Stream, JsonSerializerOptions, CancellationToken)"/>
    /// so the JSON array is streamed element-by-element rather than
    /// fully buffered (the export is ~2.4&#160;MB / ~2,360 machines as of
    /// 2026-05-04, comfortably small but no reason to materialize all
    /// at once).
    /// </remarks>
    public async IAsyncEnumerable<OpdbMachineDto> StreamAllMachinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(_options.BaseUrl, UriKind.Absolute), "export");
        Logger.LogDebug("OPDB: fetching catalog from {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var dto in JsonSerializer
            .DeserializeAsyncEnumerable<OpdbMachineDto>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            if (dto is not null)
            {
                yield return dto;
            }
        }
    }

    /// <summary>
    /// Convenience: fetches a single OPDB machine by its OPDB ID.
    /// Returns null if OPDB returns 404. Other non-success statuses
    /// throw.
    /// </summary>
    public async Task<OpdbMachineDto?> GetMachineAsync(string opdbId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);

        var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
        var url = new Uri(baseUri, $"machines/{Uri.EscapeDataString(opdbId)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<OpdbMachineDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_options.ApiToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }
    }
}
