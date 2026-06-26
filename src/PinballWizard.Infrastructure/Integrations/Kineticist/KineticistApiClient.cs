using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Integrations.Kineticist;

/// <summary>
/// Read surface of the Kineticist games API (ADR-0043 Tier A). Extracted as an
/// interface so the resolver can be unit-tested without HTTP.
/// </summary>
public interface IKineticistApiClient
{
    /// <summary>Fetch a game by Kineticist slug; null when unknown or edition-less.</summary>
    Task<KineticistGameMatch?> GetGameBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Title search (<c>?q=</c>); up to <paramref name="limit"/> name+slug refs.</summary>
    Task<IReadOnlyList<KineticistGameRef>> SearchGamesAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Client for the Kineticist public API (v1), ADR-0043 Tier A. Resolves a
/// game by slug or title search and exposes its OPDB-keyed editions — the
/// join key our machine catalog already uses.
/// </summary>
/// <remarks>
/// <para>
/// Like every other outbound integration in this repo (e.g. <c>OpdbClient</c>),
/// all requests route through <see cref="PoliteScraperBase"/> / the politeness
/// gate (LOCKED invariant) — this also keeps us comfortably inside Kineticist's
/// published rate tiers (free 1k/day). Authenticates with the
/// <c>ki_live_</c> bearer key from <see cref="KineticistOptions.ApiKey"/>.
/// </para>
/// <para>
/// Verified against the live API 2026-06-26: <c>GET /games/{slug}</c> returns
/// <c>{ data: { slug, name, opdb_id, editions: [{ opdb_id, name }, …] } }</c>;
/// <c>GET /games?q={terms}</c> returns <c>{ data: [{ name, slug }, …] }</c>.
/// </para>
/// </remarks>
public sealed class KineticistApiClient : PoliteScraperBase, IKineticistApiClient
{
    private readonly HttpClient _http;
    private readonly KineticistOptions _options;
    private readonly Uri _apiBase;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Initializes a new <see cref="KineticistApiClient"/>.</summary>
    public KineticistApiClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<KineticistOptions> options,
        ILogger<KineticistApiClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        // Normalize to a directory base so relative "games/{slug}" resolves
        // under "…/api/v1/" rather than replacing the last path segment.
        _apiBase = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    /// <summary>
    /// Fetches a game by its Kineticist slug and returns its canonical
    /// slug/name plus the OPDB ids of every edition. Returns <see langword="null"/>
    /// when the slug is unknown (HTTP 404) or the record carries no editions.
    /// </summary>
    public async Task<KineticistGameMatch?> GetGameBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var url = new Uri(_apiBase, $"games/{Uri.EscapeDataString(slug)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        using var response = await SendPolitelyAsync(_http, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<KineticistGameDetailResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var data = body?.Data;
        if (data is null || string.IsNullOrWhiteSpace(data.Slug))
        {
            return null;
        }

        var editionOpdbIds = (data.Editions ?? [])
            .Select(e => e.OpdbId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (editionOpdbIds.Length == 0)
        {
            Logger.LogDebug(
                "Kineticist API: game '{Slug}' resolved but carries no edition OPDB ids; cannot link.", data.Slug);
            return null;
        }

        return new KineticistGameMatch(data.Slug, data.Name ?? data.Slug, editionOpdbIds);
    }

    /// <summary>
    /// Title search (<c>GET /games?q=</c>). Returns up to <paramref name="limit"/>
    /// game references (name + slug) ordered by the API's relevance, or an empty
    /// list when nothing matches. Callers resolve the chosen slug via
    /// <see cref="GetGameBySlugAsync"/> to obtain edition OPDB ids.
    /// </summary>
    public async Task<IReadOnlyList<KineticistGameRef>> SearchGamesAsync(string query, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var url = new Uri(_apiBase, $"games?q={Uri.EscapeDataString(query)}&limit={limit}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        using var response = await SendPolitelyAsync(_http, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<KineticistGameListResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return (body?.Data ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g.Slug))
            .Select(g => new KineticistGameRef(g.Name ?? g.Slug!, g.Slug!))
            .ToArray();
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }
}
