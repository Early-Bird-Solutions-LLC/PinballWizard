using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Typed HTTP client for the OPDB catalog via Match Play (CDN export +
/// optional entry API). Extends <see cref="PoliteScraperBase"/> so
/// outbound requests flow through the politeness gate.
/// </summary>
/// <remarks>
/// Since 2026-10-01 opdb.org <c>/api/*</c> is shut down (see ADR-0059).
/// Catalog sync uses the Match Play OPDB V1 CDN export exclusively —
/// there is no silent fallback to the retired <c>/api/export</c> path.
/// </remarks>
public sealed class OpdbClient : PoliteScraperBase, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OpdbOptions _options;

    // Group-title cache: segment → title (or null = confirmed miss).
    // Seeded from the export's machineGroups array during
    // StreamAllMachinesAsync; also loaded/persisted on disk.
    private readonly SemaphoreSlim _groupCacheLock = new(1, 1);
    private Dictionary<string, string?>? _groupTitleCache;
    private bool _groupCacheLoadWarned;
    private bool _exportCatalogLoaded;
    private bool _disposed;

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
    /// Streams the full OPDB machine catalog (base machines then aliases)
    /// from the Match Play V1 CDN export. Group titles from the same
    /// envelope are seeded into the group-title cache before any item
    /// is yielded so <see cref="GetMachineGroupTitleAsync"/> hits memory
    /// during the same sync pass.
    /// </summary>
    public async IAsyncEnumerable<OpdbMachineDto> StreamAllMachinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sourceStream = await OpenExportStreamAsync(cancellationToken).ConfigureAwait(false);

        OpdbExportEnvelopeDto envelope;
        await using (sourceStream)
        {
            envelope = await ParseExportEnvelopeAsync(sourceStream, cancellationToken).ConfigureAwait(false);
        }

        await SeedGroupTitlesFromExportAsync(envelope.MachineGroups, cancellationToken).ConfigureAwait(false);

        foreach (var dto in envelope.Machines.Where(static d => d is not null))
        {
            yield return dto!;
        }

        foreach (var dto in envelope.Aliases.Where(static d => d is not null))
        {
            yield return dto!;
        }
    }

    private async Task<Stream> OpenExportStreamAsync(CancellationToken cancellationToken)
    {
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

        var exportUrl = ResolveExportUrl();
        Logger.LogDebug("OPDB: fetching catalog from {Url}", exportUrl);

        byte[] bytes;
        using (var request = new HttpRequestMessage(HttpMethod.Get, exportUrl))
        {
            // CDN export is public — do not attach the Match Play API bearer
            // token (wrong credential surface; would also leak the secret
            // to DigitalOcean Spaces request logs).
            using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"OPDB Match Play export GET {exportUrl} failed with {(int)response.StatusCode} ({response.StatusCode}). " +
                    "The retired opdb.org /api/export path is not used as a fallback (ADR-0059).",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(cachePath))
        {
            WriteCacheFile(cachePath, bytes);
        }

        return new MemoryStream(bytes, writable: false);
    }

    private Uri ResolveExportUrl()
    {
        if (string.IsNullOrWhiteSpace(_options.ExportUrl))
        {
            throw new InvalidOperationException(
                "Opdb:ExportUrl is required. Set it to the Match Play OPDB V1 CDN URL " +
                "(https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json). " +
                "The retired opdb.org /api/export endpoint is not a valid substitute (ADR-0059).");
        }

        if (!Uri.TryCreate(_options.ExportUrl, UriKind.Absolute, out var absolute)
            || (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Opdb:ExportUrl must be an absolute http(s) URL; got '{_options.ExportUrl}'.");
        }

        return absolute;
    }

    private async Task<OpdbExportEnvelopeDto> ParseExportEnvelopeAsync(
        Stream sourceStream,
        CancellationToken cancellationToken)
    {
        OpdbExportEnvelopeDto? envelope;
        try
        {
            envelope = await JsonSerializer
                .DeserializeAsync<OpdbExportEnvelopeDto>(sourceStream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "OPDB export JSON could not be parsed as the Match Play V1 envelope " +
                "({ machineGroups, machines, aliases }). " +
                "Confirm Opdb:ExportUrl points at latest-opdb.json (not opdb-v2.json / slim / the retired opdb.org /api/export array).",
                ex);
        }

        if (envelope is null)
        {
            throw new InvalidOperationException(
                "OPDB export deserialized to null. Confirm Opdb:ExportUrl returns the Match Play V1 envelope.");
        }

        // Fail closed on the wrong document shape. A top-level JSON array
        // (legacy opdb.org /api/export) deserializes as an empty object under
        // this DTO type in some paths, or throws above; an opdb-v2 payload
        // has `entries` and leaves Machines/Aliases at default empty lists.
        // Distinguishing "legitimately empty catalog" from "wrong URL" is
        // done by requiring the property presence via a second peek only when
        // both machine collections are empty AND machineGroups is empty —
        // an empty-but-valid V1 file is vanishingly unlikely (~2.2k machines
        // today). Operators pointing at V2 would sync zero machines silently
        // without this guard.
        if (envelope.Machines.Count == 0 && envelope.Aliases.Count == 0 && envelope.MachineGroups.Count == 0)
        {
            throw new InvalidOperationException(
                "OPDB export envelope has empty machines, aliases, and machineGroups. " +
                "This usually means Opdb:ExportUrl points at the wrong document " +
                "(e.g. opdb-v2.json with an `entries` array, or a non-OPDB payload). " +
                "Expected Match Play V1: https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json");
        }

        Logger.LogInformation(
            "OPDB: parsed Match Play V1 export ({MachineCount} machines, {AliasCount} aliases, {GroupCount} groups).",
            envelope.Machines.Count, envelope.Aliases.Count, envelope.MachineGroups.Count);

        return envelope;
    }

    private async Task SeedGroupTitlesFromExportAsync(
        IReadOnlyList<OpdbMachineGroupDto> groups,
        CancellationToken cancellationToken)
    {
        await _groupCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureGroupCacheLoadedAsync().ConfigureAwait(false);

            var seeded = 0;
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.OpdbId))
                {
                    continue;
                }

                // Only accept real group records; defensive against a
                // mis-shaped payload stuffing machines into machineGroups.
                if (!group.IsMachineGroup)
                {
                    _groupTitleCache![group.OpdbId] = null;
                    continue;
                }

                _groupTitleCache![group.OpdbId] =
                    string.IsNullOrWhiteSpace(group.Name) ? null : group.Name;
                seeded++;
            }

            _exportCatalogLoaded = true;
            PersistGroupCacheBestEffort();

            Logger.LogInformation(
                "OPDB: seeded {SeededCount} group titles from export machineGroups (catalog loaded for this process).",
                seeded);
        }
        finally
        {
            _groupCacheLock.Release();
        }
    }

    /// <summary>
    /// Fetches a single OPDB machine by its OPDB ID via the Match Play
    /// entry API. Returns null on 404. Other non-success statuses throw.
    /// Prefer the bulk export for catalog sync.
    /// </summary>
    public async Task<OpdbMachineDto?> GetMachineAsync(string opdbId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);

        var url = BuildEntryUrl(opdbId);
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
    /// Resolves an OPDB machine-group record by segment. After a successful
    /// <see cref="StreamAllMachinesAsync"/> in this process, titles come
    /// from the export-seeded cache (no HTTP). Otherwise optionally hits
    /// Match Play <c>GET /api/opdb/entry/{segment}</c>. Returns null when
    /// the segment is absent or not a group.
    /// </summary>
    public async Task<OpdbMachineGroupDto?> GetMachineGroupAsync(string groupSegment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupSegment);

        await _groupCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureGroupCacheLoadedAsync().ConfigureAwait(false);
            if (_groupTitleCache!.TryGetValue(groupSegment, out var cachedTitle))
            {
                if (cachedTitle is null)
                {
                    return null;
                }

                return new OpdbMachineGroupDto
                {
                    OpdbId = groupSegment,
                    IsMachineGroup = true,
                    Name = cachedTitle,
                };
            }

            if (_exportCatalogLoaded)
            {
                // Complete V1 export did not contain this segment — confirmed miss.
                _groupTitleCache[groupSegment] = null;
                PersistGroupCacheBestEffort();
                return null;
            }
        }
        finally
        {
            _groupCacheLock.Release();
        }

        var url = BuildEntryUrl(groupSegment);
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

        return group is { IsMachineGroup: true } ? group : null;
    }

    /// <summary>
    /// Fetches an OPDB group title by segment, consulting the persistent
    /// on-disk / export-seeded cache first.
    /// </summary>
    public async Task<string?> GetMachineGroupTitleAsync(string groupSegment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupSegment);

        await _groupCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureGroupCacheLoadedAsync().ConfigureAwait(false);

            if (_groupTitleCache!.TryGetValue(groupSegment, out var cached))
            {
                return cached;
            }

            if (_exportCatalogLoaded)
            {
                _groupTitleCache[groupSegment] = null;
                PersistGroupCacheBestEffort();
                return null;
            }
        }
        finally
        {
            _groupCacheLock.Release();
        }

        var group = await GetMachineGroupAsync(groupSegment, cancellationToken).ConfigureAwait(false);
        var resolved = string.IsNullOrWhiteSpace(group?.Name) ? null : group!.Name;

        await _groupCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _groupTitleCache![groupSegment] = resolved;
            PersistGroupCacheBestEffort();
        }
        finally
        {
            _groupCacheLock.Release();
        }

        return resolved;
    }

    private Uri BuildEntryUrl(string opdbId)
    {
        var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
        // Ensure trailing-slash semantics so "api/opdb/entry/X" appends under BaseUrl.
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
        }

        return new Uri(baseUri, $"api/opdb/entry/{Uri.EscapeDataString(opdbId)}");
    }

    private async Task EnsureGroupCacheLoadedAsync()
    {
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
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch (Exception) { /* best-effort cleanup; ignore */ }
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _groupCacheLock.Dispose();
    }
}
