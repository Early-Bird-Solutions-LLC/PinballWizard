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
    /// endpoint (404 against the live API) — see DL-0003.
    /// <para>
    /// Cache behavior: when <see cref="OpdbOptions.ExportCachePath"/> is
    /// set and a fresh cache file exists (modified within
    /// <see cref="OpdbOptions.ExportCacheTtlSeconds"/>), this method
    /// streams from the cache instead of hitting the network. OPDB's
    /// published policy on <c>/api/export</c> is once-per-hour; the
    /// cache eliminates the rate-limit problem for any repeat call
    /// within the TTL window. Cache miss: fetch + buffer + write to
    /// disk + yield from memory. Persist failures degrade gracefully
    /// (logged, fetch still succeeds).
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<OpdbMachineDto> StreamAllMachinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sourceStream = await OpenExportStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (sourceStream)
        {
            await foreach (var dto in JsonSerializer
                .DeserializeAsyncEnumerable<OpdbMachineDto>(sourceStream, JsonOptions, cancellationToken)
                .ConfigureAwait(false))
            {
                if (dto is not null)
                {
                    yield return dto;
                }
            }
        }
    }

    private async Task<Stream> OpenExportStreamAsync(CancellationToken cancellationToken)
    {
        // Cache hit short-circuit. When the cache path is configured AND
        // the file is fresh enough (mtime within TTL), open it for streaming
        // and return — no network call. Disabling the cache (empty path) or
        // setting Ttl=0 forces the network path. The mtime is set by
        // File.WriteAllBytesAsync below on the previous fetch.
        var cachePath = _options.ExportCachePath;
        var ttl = TimeSpan.FromSeconds(_options.ExportCacheTtlSeconds);
        if (!string.IsNullOrWhiteSpace(cachePath) && ttl > TimeSpan.Zero && File.Exists(cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            if (age < ttl)
            {
                Logger.LogInformation(
                    "OPDB: using cached export from {Path} (age {AgeSeconds:N0}s, ttl {TtlSeconds:N0}s).",
                    cachePath, age.TotalSeconds, ttl.TotalSeconds);
                try
                {
                    return File.OpenRead(cachePath);
                }
                catch (IOException ex)
                {
                    // Cache file unreadable (locked, permission flip, etc.).
                    // Fall through to the network path; do NOT delete the
                    // file — the next clean fetch will overwrite it
                    // atomically below. Logging at warning is enough.
                    Logger.LogWarning(
                        ex,
                        "OPDB: cache file at {Path} could not be opened; falling back to network fetch.",
                        cachePath);
                }
            }
            else
            {
                Logger.LogInformation(
                    "OPDB: cache at {Path} is stale (age {AgeSeconds:N0}s > ttl {TtlSeconds:N0}s); refetching.",
                    cachePath, age.TotalSeconds, ttl.TotalSeconds);
            }
        }

        // Cache miss / disabled: fetch from network, buffer, persist
        // best-effort, return a memory stream over the buffer.
        var url = new Uri(new Uri(_options.BaseUrl, UriKind.Absolute), "export");
        Logger.LogDebug("OPDB: fetching catalog from {Url}", url);

        byte[] bytes;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            ApplyAuth(request);
            using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(cachePath))
        {
            // Atomic write: write to a sibling `.tmp` file, then `File.Move`
            // it into the final path with overwrite. Crashes / power loss
            // mid-write leave the prior cache file intact; the consumer
            // never sees a torn JSON. The .tmp file is also cleaned up if
            // the move fails (best-effort).
            var tmpPath = cachePath + ".tmp";
            try
            {
                var dir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllBytesAsync(tmpPath, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(tmpPath, cachePath, overwrite: true);
                Logger.LogInformation(
                    "OPDB: persisted export cache to {Path} ({Bytes:N0} bytes).",
                    cachePath, bytes.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cache persist is best-effort. A write failure (path
                // unwritable, disk full, etc.) is logged but not fatal —
                // the caller still gets the in-memory response. Best-effort
                // cleanup of the temp file in case the failure was at the
                // Move step.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
                Logger.LogWarning(
                    ex,
                    "OPDB: failed to persist export cache to {Path}; the in-memory response is unaffected.",
                    cachePath);
            }
        }

        return new MemoryStream(bytes, writable: false);
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
