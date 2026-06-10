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
public sealed class OpdbClient : PoliteScraperBase, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OpdbOptions _options;

    // Group-title cache: segment → title (or null = confirmed 404 / non-group).
    // Loaded lazily from disk on first GetMachineGroupTitleAsync call; persisted
    // after each new entry so a fresh client in the NEXT sync run benefits
    // immediately. Lock protects concurrent reads/writes if the client is ever
    // shared across tasks (currently single-threaded in ResolveGroupTitleAsync,
    // but defensive).
    private readonly SemaphoreSlim _groupCacheLock = new(1, 1);
    private Dictionary<string, string?>? _groupTitleCache;
    private bool _groupCacheLoadWarned;

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
        // WriteCacheFile on the previous fetch.
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
            WriteCacheFile(cachePath, bytes);
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

    /// <summary>
    /// Fetches an OPDB <c>is_machine_group</c> record by its group
    /// segment (the leading part of an OPDB ID before the first hyphen,
    /// e.g. <c>GweeP</c>). The group record carries the clean franchise
    /// title and is NOT present in the bulk <c>/api/export</c> feed —
    /// this per-segment call is the only way to obtain it. Returns null
    /// if OPDB returns 404 (no group record for that segment) or if the
    /// returned record is not actually a group (defensive: the
    /// <c>machines/{id}</c> path also serves machine/alias records, so a
    /// caller passing a full machine ID would otherwise get a non-group
    /// body). Other non-success statuses throw.
    /// </summary>
    public async Task<OpdbMachineGroupDto?> GetMachineGroupAsync(string groupSegment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupSegment);

        var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
        var url = new Uri(baseUri, $"machines/{Uri.EscapeDataString(groupSegment)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);

        using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var group = await response.Content
            .ReadFromJsonAsync<OpdbMachineGroupDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        // The machines/{id} endpoint is polymorphic — it serves machine,
        // alias, and group records. Only return a body that is actually a
        // group so a mis-passed full machine ID degrades to null rather
        // than yielding a group DTO with IsMachineGroup=false.
        return group is { IsMachineGroup: true } ? group : null;
    }

    /// <summary>
    /// Fetches an OPDB <c>is_machine_group</c> record by its group
    /// segment, consulting the persistent on-disk cache first. A cache
    /// hit (including a null "negative" entry from a prior 404) avoids
    /// the polite HTTP GET entirely — fewer requests is more polite.
    /// Cache misses hit the network, and the result (title OR null) is
    /// persisted best-effort so subsequent runs do not re-fetch.
    /// </summary>
    /// <remarks>
    /// The in-memory dictionary in <see cref="OpdbSyncService"/> remains
    /// a harmless per-run fast path; this disk-backed cache is the
    /// cross-run layer that eliminates the ~1,200-request re-fetch
    /// problem observed on 2026-06-10 (~3.5 h run time).
    /// </remarks>
    public async Task<string?> GetMachineGroupTitleAsync(string groupSegment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupSegment);

        await _groupCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureGroupCacheLoadedAsync().ConfigureAwait(false);

            if (_groupTitleCache!.TryGetValue(groupSegment, out var cached))
            {
                // null = confirmed 404/non-group; return it directly — do NOT re-fetch.
                return cached;
            }
        }
        finally
        {
            _groupCacheLock.Release();
        }

        // Cache miss: hit the network (outside the lock — politeness gate
        // may hold for 10 s and we must not block the lock that long).
        var group = await GetMachineGroupAsync(groupSegment, cancellationToken).ConfigureAwait(false);
        var resolved = string.IsNullOrWhiteSpace(group?.Name) ? null : group!.Name;

        await _groupCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureGroupCacheLoadedAsync().ConfigureAwait(false);
            // Another caller may have populated this entry while we held the
            // network call outside the lock. Last-writer-wins is fine for
            // stable franchise names.
            _groupTitleCache![groupSegment] = resolved;
            PersistGroupCacheBestEffort();
        }
        finally
        {
            _groupCacheLock.Release();
        }

        return resolved;
    }

    private async Task EnsureGroupCacheLoadedAsync()
    {
        // Must be called under _groupCacheLock.
        if (_groupTitleCache is not null)
        {
            return;
        }

        _groupTitleCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        var cachePath = _options.GroupTitleCachePath;
        var ttl = TimeSpan.FromSeconds(_options.GroupTitleCacheTtlSeconds);
        if (string.IsNullOrWhiteSpace(cachePath) || ttl == TimeSpan.Zero || !File.Exists(cachePath))
        {
            return;
        }

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
        if (age >= ttl)
        {
            Logger.LogInformation(
                "OPDB: group-title cache at {Path} is stale (age {AgeSeconds:N0}s > ttl {TtlSeconds:N0}s); will refetch all segments.",
                cachePath, age.TotalSeconds, ttl.TotalSeconds);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cachePath).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions);
            if (loaded is not null)
            {
                foreach (var (k, v) in loaded)
                {
                    _groupTitleCache[k] = v;
                }
                Logger.LogInformation(
                    "OPDB: loaded {Count} group-title entries from cache {Path} (age {AgeSeconds:N0}s).",
                    _groupTitleCache.Count, cachePath, age.TotalSeconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_groupCacheLoadWarned)
            {
                _groupCacheLoadWarned = true;
                Logger.LogWarning(
                    ex,
                    "OPDB: failed to load group-title cache from {Path}; starting with empty in-memory cache.",
                    cachePath);
            }
        }
    }

    private void PersistGroupCacheBestEffort()
    {
        // Must be called under _groupCacheLock.
        // Persist on each new entry: new entries are rare in steady state
        // (~hundreds of distinct segments, essentially stable after first
        // run), so per-entry persistence is negligible vs the 10-s/call
        // cost of re-fetching. If this proves noisy under high-churn, move
        // to end-of-run persistence.
        var cachePath = _options.GroupTitleCachePath;
        if (string.IsNullOrWhiteSpace(cachePath) || _groupTitleCache is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(_groupTitleCache, JsonOptions);
            WriteCacheFile(cachePath, System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "OPDB: failed to persist group-title cache to {Path}; the in-memory cache is unaffected.",
                cachePath);
        }
    }

    /// <summary>
    /// Atomically writes <paramref name="bytes"/> to <paramref name="cachePath"/>
    /// via a sibling <c>.tmp</c> file + rename. Crashes mid-write leave the
    /// prior file intact. A pre-existing destination file is explicitly deleted
    /// before the rename so that <c>File.Move</c> does not use
    /// <c>MOVEFILE_REPLACE_EXISTING</c> semantics — on Windows, that flag can
    /// return <c>ERROR_ACCESS_DENIED</c> when the destination exists and carries
    /// a read-only attribute (e.g. set by OneDrive sync) or is temporarily
    /// held open by another process, even if the caller has write permission on
    /// the directory. Separating "delete old" from "place new" makes each step
    /// independently auditable and avoids the ambiguous
    /// <see cref="UnauthorizedAccessException"/> that was observed in the
    /// 2026-06-10 live sync run (CWD = <c>C:\Users\JimKeeley</c>).
    /// </summary>
    private void WriteCacheFile(string cachePath, byte[] bytes)
    {
        var tmpPath = cachePath + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(tmpPath, bytes);

            // Delete the existing destination before moving so the rename
            // never needs to overwrite — eliminates MOVEFILE_REPLACE_EXISTING
            // and the associated UnauthorizedAccessException on Windows when
            // the destination exists and has a read-only attribute (e.g. set
            // by OneDrive sync) or is temporarily held open by a sync process.
            // Clearing ReadOnly before Delete avoids a second UnauthorizedAccessException
            // from the Delete itself.
            if (File.Exists(cachePath))
            {
                var attrs = File.GetAttributes(cachePath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(cachePath, attrs & ~FileAttributes.ReadOnly);
                }
                File.Delete(cachePath);
            }

            File.Move(tmpPath, cachePath);

            Logger.LogInformation(
                "OPDB: persisted cache to {Path} ({Bytes:N0} bytes).",
                cachePath, bytes.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cache persist is best-effort. A write failure (path unwritable,
            // disk full, etc.) is logged but not fatal — the caller still has
            // the in-memory copy. Best-effort cleanup of the temp file in case
            // the failure was at the Move step.
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch (IOException) { /* best-effort cleanup; ignore */ }
            Logger.LogWarning(
                ex,
                "OPDB: failed to persist cache to {Path}; the in-memory response is unaffected.",
                cachePath);
        }
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_options.ApiToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _groupCacheLock.Dispose();
    }
}
