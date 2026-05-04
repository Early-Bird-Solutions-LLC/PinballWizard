using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Integrations.PinballMap;

/// <summary>
/// Typed HTTP client for the Pinball Map (pinballmap.com) public API.
/// Extends <see cref="PoliteScraperBase"/> so requests flow through the
/// politeness gate with the same per-origin throttle + 429-backoff
/// invariants as every other source.
/// </summary>
/// <remarks>
/// The Pinball Map API is public — no authentication required. Requests
/// carry the project's identifying User-Agent (per <c>feedback_polite_scraping.md</c>)
/// and honor a configurable on-disk cache to avoid re-fetching the same
/// region within the TTL window. The cache mirrors the OPDB pattern
/// (atomic <c>.tmp</c> + <see cref="File.Move(string,string,bool)"/>) so a
/// crash mid-write leaves the prior cache intact.
/// </remarks>
public sealed class PinballMapClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly PinballMapOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Initializes a new <see cref="PinballMapClient"/>.</summary>
    public PinballMapClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<PinballMapOptions> pinballMapOptions,
        ILogger<PinballMapClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(pinballMapOptions);
        _httpClient = httpClient;
        _options = pinballMapOptions.Value;
    }

    /// <summary>
    /// Fetches every location in <paramref name="region"/> (e.g.,
    /// <c>chicago</c>, <c>portland</c>, <c>la</c>). Returns the parsed
    /// <see cref="PinballMapLocationDto"/> list; the caller decides
    /// whether to filter, map, or persist.
    /// </summary>
    /// <remarks>
    /// Cache behavior: when <see cref="PinballMapOptions.CacheDirectory"/>
    /// is set and a fresh cache file exists for the region (modified
    /// within <see cref="PinballMapOptions.CacheTtlSeconds"/>), this
    /// method reads from the cache instead of hitting the network.
    /// Cache miss: fetch + buffer + write to disk + return from
    /// memory. Persist failures degrade gracefully (logged, fetch still
    /// succeeds).
    /// </remarks>
    public async Task<IReadOnlyList<PinballMapLocationDto>> GetLocationsByRegionAsync(
        string region,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var regionAttr = new KeyValuePair<string, object?>("pinwiz.pinballmap.region", region);
        using var activity = PinballWizardTelemetry.ActivitySource.StartActivity(
            PinballWizardTelemetry.PinballMapFetchActivity, ActivityKind.Client);
        activity?.SetTag(regionAttr.Key, regionAttr.Value);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var bytes = await GetRegionLocationsBytesAsync(region, cancellationToken).ConfigureAwait(false);

            var response = JsonSerializer.Deserialize<PinballMapLocationsResponse>(bytes, JsonOptions);
            if (response is null)
            {
                // A successful HTTP 200 that deserialized to null indicates
                // an unexpected response shape (empty body, bare null
                // literal). Returning empty here is safer than throwing —
                // the integration is best-effort and a single empty region
                // must not abort a batch of citations. Future runs will
                // refetch (cache TTL is measured against the on-disk write,
                // so a malformed body that was nevertheless persisted will
                // be re-attempted on its next expiration).
                Logger.LogWarning(
                    "PinballMap: region '{Region}' returned a null/empty body; treating as empty.",
                    region);
                PinballWizardTelemetry.PinballMapFetched.Add(1, regionAttr);
                return [];
            }

            PinballWizardTelemetry.PinballMapFetched.Add(1, regionAttr);
            PinballWizardTelemetry.PinballMapLocations.Add(response.Locations.Count, regionAttr);
            activity?.SetTag("pinwiz.pinballmap.locations", response.Locations.Count);
            return response.Locations;
        }
        catch (Exception ex)
        {
            PinballWizardTelemetry.PinballMapFailed.Add(1, regionAttr);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.PinballMapFetchDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, regionAttr);
            activity?.SetTag("pinwiz.pinballmap.fetch.duration_ms", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<byte[]> GetRegionLocationsBytesAsync(string region, CancellationToken cancellationToken)
    {
        // Cache hit short-circuit. When the cache directory is configured
        // AND a per-region file is fresh enough (mtime within TTL), read
        // its bytes and return — no network call. Empty CacheDirectory
        // disables the cache entirely; CacheTtlSeconds=0 forces every call
        // to bypass the cache (file is still written on success).
        var cachePath = TryGetCachePath(region);
        var ttl = TimeSpan.FromSeconds(_options.CacheTtlSeconds);
        if (cachePath is not null && ttl > TimeSpan.Zero && File.Exists(cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            if (age < ttl)
            {
                Logger.LogInformation(
                    "PinballMap: using cached region '{Region}' from {Path} (age {AgeSeconds:N0}s, ttl {TtlSeconds:N0}s).",
                    region, cachePath, age.TotalSeconds, ttl.TotalSeconds);
                try
                {
                    return await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Cache file unreadable (locked, permission flip, etc.).
                    // Fall through to the network path; do NOT delete the
                    // file — the next clean fetch will overwrite it
                    // atomically below. Logging at warning is enough.
                    Logger.LogWarning(
                        ex,
                        "PinballMap: cache file at {Path} could not be opened; falling back to network fetch.",
                        cachePath);
                }
            }
            else
            {
                Logger.LogInformation(
                    "PinballMap: cache for region '{Region}' at {Path} is stale (age {AgeSeconds:N0}s > ttl {TtlSeconds:N0}s); refetching.",
                    region, cachePath, age.TotalSeconds, ttl.TotalSeconds);
            }
        }

        // Cache miss / disabled: fetch from network, buffer, persist
        // best-effort, return the buffer.
        var url = BuildRegionUrl(region);
        Logger.LogDebug("PinballMap: fetching region '{Region}' from {Url}", region, url);

        byte[] bytes;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        if (cachePath is not null)
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
                    "PinballMap: persisted region '{Region}' cache to {Path} ({Bytes:N0} bytes).",
                    region, cachePath, bytes.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cache persist is best-effort. A write failure (path
                // unwritable, disk full, etc.) is logged but not fatal —
                // the caller still gets the in-memory response. Best-effort
                // cleanup of the temp file in case the failure was at the
                // Move step.
                try
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
                catch (Exception cleanupEx)
                {
                    Logger.LogDebug(
                        cleanupEx,
                        "PinballMap: best-effort cleanup of temp cache file {TmpPath} failed; ignoring.",
                        tmpPath);
                }
                Logger.LogWarning(
                    ex,
                    "PinballMap: failed to persist region '{Region}' cache to {Path}; the in-memory response is unaffected.",
                    region, cachePath);
            }
        }

        return bytes;
    }

    private string? TryGetCachePath(string region)
    {
        var dir = _options.CacheDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        // Sanitize region for use as a filename component. Pinball Map
        // region slugs are ASCII (e.g., "chicago", "portland", "la"), but
        // the API tolerates any non-empty path segment so a defensive
        // sanitize avoids surprises if a caller ever passes a weird value.
        var safe = string.Concat(region
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-'));
        return Path.Combine(dir, $"locations-{safe}.json");
    }

    private Uri BuildRegionUrl(string region)
    {
        // The base URL is configured with a trailing slash so relative
        // resolution lands on /api/v1/region/... regardless of host.
        var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
        return new Uri(baseUri, $"region/{Uri.EscapeDataString(region.Trim().ToLowerInvariant())}/locations.json");
    }
}
