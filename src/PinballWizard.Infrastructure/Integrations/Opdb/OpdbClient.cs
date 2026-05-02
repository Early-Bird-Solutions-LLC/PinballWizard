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
    /// Streams the full OPDB machine catalog in pages. Each yielded
    /// record is a parsed <see cref="OpdbMachineDto"/>; the caller
    /// decides whether to filter, map, or upsert.
    /// </summary>
    /// <remarks>
    /// OPDB's <c>/api/machines/changelog</c>-style endpoints support
    /// pagination via standard <c>?page=</c> + <c>?page_size=</c>
    /// query parameters. We page until an empty page comes back.
    /// </remarks>
    public async IAsyncEnumerable<OpdbMachineDto> StreamAllMachinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            var url = new Uri(BuildPagedUrl(page), UriKind.Absolute);
            Logger.LogDebug("OPDB: fetching page {Page} from {Url}", page, url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request);

            using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var batch = await response.Content
                .ReadFromJsonAsync<OpdbMachineDto[]>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (batch is null || batch.Length == 0)
            {
                yield break;
            }

            foreach (var dto in batch)
            {
                yield return dto;
            }

            if (batch.Length < _options.PageSize)
            {
                yield break;
            }

            page++;
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

    private string BuildPagedUrl(int page)
    {
        var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
        var endpoint = new Uri(baseUri, "machines");
        var separator = endpoint.Query.Length > 0 ? "&" : "?";
        return $"{endpoint}{separator}page={page}&page_size={_options.PageSize}";
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_options.ApiToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }
    }
}
