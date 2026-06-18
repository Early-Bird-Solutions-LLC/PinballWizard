using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Landing;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;

namespace PinballWizard.Infrastructure.Landing;

// Infrastructure implementation of ISystemStatusProvider. Composes the
// three Azure dependency probes (Foundry, AI Search, Cosmos) into a single
// SystemStatus value and caches the result for SystemStatusOptions.CacheTtl
// (default 30 seconds) so the public /api/wizard/landing endpoint doesn't
// hammer Azure on every anonymous page load.
//
// Design decisions:
//
//   Parallelism: all three probes run via Task.WhenAll so the combined
//   latency is max(probe_time) not the sum. Each probe costs ~1 RU and a
//   few hundred ms round-trip to the respective Azure service.
//
//   Mapping: Success → true, failure → false, exception / probe not
//   configured → null ("unknown"). The frontend treats null as "unknown"
//   and renders a neutral indicator rather than red/green.
//
//   Stampede safety: IMemoryCache.GetOrCreateAsync does NOT guarantee
//   single-flight execution in .NET — multiple concurrent callers on a
//   cache miss can all invoke the factory simultaneously. We guard with a
//   SemaphoreSlim(1, 1): the first caller to acquire the semaphore runs
//   the probes; subsequent concurrent callers block on the semaphore and
//   then read from the newly-populated cache. This means 10 concurrent
//   requests after a TTL expiry trigger exactly one probe round-trip.
//
//   Optional dependencies: IAzureFoundrySmokeProbe and
//   IAzureAiSearchSmokeProbe are injected as nullable because those
//   integrations are not wired when the Api starts without Foundry or
//   AI Search configured (e.g., local dev). When absent, the corresponding
//   field is null ("unknown"). CosmosCanaryProbe is similarly optional —
//   when Cosmos is not wired, CosmosHealthy is null.
//
// Lives in Infrastructure (not Application) because it depends on the
// two Infrastructure smoke-probe interfaces (IAzureFoundrySmokeProbe and
// IAzureAiSearchSmokeProbe). The interface (ISystemStatusProvider) lives
// in Application to preserve Clean Architecture's dependency direction.
public sealed class SystemStatusProvider : ISystemStatusProvider, IDisposable
{
    private const string CacheKey = "SystemStatusProvider:status";

    private readonly IAzureFoundrySmokeProbe? _foundryProbe;
    private readonly IAzureAiSearchSmokeProbe? _aiSearchProbe;
    private readonly ICosmosCanaryProbe? _cosmosProbe;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<SystemStatusProvider> _logger;

    // Single-flight gate: prevents concurrent callers from all running the
    // probe logic simultaneously on a cache miss (stampede protection).
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SystemStatusProvider(
        IMemoryCache cache,
        IOptions<SystemStatusOptions> options,
        ILogger<SystemStatusProvider> logger,
        IAzureFoundrySmokeProbe? foundryProbe = null,
        IAzureAiSearchSmokeProbe? aiSearchProbe = null,
        ICosmosCanaryProbe? cosmosProbe = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _cache = cache;
        _cacheTtl = options.Value.CacheTtl;
        _logger = logger;
        _foundryProbe = foundryProbe;
        _aiSearchProbe = aiSearchProbe;
        _cosmosProbe = cosmosProbe;
    }

    public void Dispose() => _refreshLock.Dispose();

    public async Task<SystemStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // Fast path: cache hit — no lock needed.
        if (_cache.TryGetValue(CacheKey, out SystemStatus? cached) && cached is not null)
        {
            return cached;
        }

        // Slow path: cache miss — acquire the semaphore so only one caller
        // runs the probes. Other concurrent callers block here; once the
        // semaphore is released they take the fast path (cache hit).
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock: a concurrent caller
            // may have populated the cache while we were waiting.
            if (_cache.TryGetValue(CacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var status = await RunProbesAsync(cancellationToken).ConfigureAwait(false);

            _cache.Set(CacheKey, status, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _cacheTtl,
            });

            return status;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<SystemStatus> RunProbesAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "SystemStatusProvider: probing Foundry={FoundryWired}, " +
            "AiSearch={AiSearchWired}, Cosmos={CosmosWired}.",
            _foundryProbe is not null,
            _aiSearchProbe is not null,
            _cosmosProbe is not null);

        var foundryTask = ProbeFoundryAsync(cancellationToken);
        var aiSearchTask = ProbeAiSearchAsync(cancellationToken);
        var cosmosTask = ProbeCosmosAsync(cancellationToken);

        await Task.WhenAll(foundryTask, aiSearchTask, cosmosTask).ConfigureAwait(false);

        var result = new SystemStatus(
            CosmosHealthy: cosmosTask.Result,
            FoundryHealthy: foundryTask.Result,
            AiSearchHealthy: aiSearchTask.Result);

        _logger.LogDebug(
            "SystemStatusProvider: probe results — " +
            "Cosmos={CosmosHealthy}, Foundry={FoundryHealthy}, AiSearch={AiSearchHealthy}.",
            result.CosmosHealthy, result.FoundryHealthy, result.AiSearchHealthy);

        return result;
    }

    private async Task<bool?> ProbeFoundryAsync(CancellationToken cancellationToken)
    {
        if (_foundryProbe is null)
        {
            return null;
        }
        try
        {
            var result = await _foundryProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or HttpRequestException
                                       or InvalidOperationException or TimeoutException)
        {
            // Probe-specific failures: Azure SDK (RequestFailedException), network
            // (HttpRequestException), misconfigured probe (InvalidOperationException),
            // or timeout. All map to null/"unknown" — not red/green on the dashboard.
            _logger.LogWarning(ex, "SystemStatusProvider: Foundry probe threw unexpectedly.");
            return null;
        }
        catch (Exception ex)
        {
            // Broad fallback catch (invariant #17 audit 2026-06-12): any exception
            // type not in the typed list above (e.g. ObjectDisposedException, NRE
            // from a misconfigured SDK) must still return null rather than letting
            // Task.WhenAll propagate the exception and crash GetStatusAsync entirely.
            // Logged at Warning so the unexpected type is visible to operators.
            _logger.LogWarning(ex,
                "SystemStatusProvider: Foundry probe threw an unexpected exception type '{ExceptionType}'. Treating as unknown.",
                ex.GetType().Name);
            return null;
        }
    }

    private async Task<bool?> ProbeAiSearchAsync(CancellationToken cancellationToken)
    {
        if (_aiSearchProbe is null)
        {
            return null;
        }
        try
        {
            var result = await _aiSearchProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or HttpRequestException
                                       or InvalidOperationException or TimeoutException)
        {
            // Probe-specific failures: Azure SDK (RequestFailedException), network
            // (HttpRequestException), misconfigured probe (InvalidOperationException),
            // or timeout. All map to null/"unknown" — not red/green on the dashboard.
            _logger.LogWarning(ex, "SystemStatusProvider: AI Search probe threw unexpectedly.");
            return null;
        }
        catch (Exception ex)
        {
            // Broad fallback catch (invariant #17 audit 2026-06-12): any exception
            // type not in the typed list above must still return null rather than
            // crashing GetStatusAsync via Task.WhenAll propagation.
            _logger.LogWarning(ex,
                "SystemStatusProvider: AI Search probe threw an unexpected exception type '{ExceptionType}'. Treating as unknown.",
                ex.GetType().Name);
            return null;
        }
    }

    private async Task<bool?> ProbeCosmosAsync(CancellationToken cancellationToken)
    {
        if (_cosmosProbe is null)
        {
            return null;
        }
        try
        {
            return await _cosmosProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is CosmosException or HttpRequestException
                                       or InvalidOperationException or TimeoutException)
        {
            // Probe-specific failures: Cosmos SDK (CosmosException), network
            // (HttpRequestException), misconfigured probe (InvalidOperationException),
            // or timeout. All map to null/"unknown" — not red/green on the dashboard.
            _logger.LogWarning(ex, "SystemStatusProvider: Cosmos canary probe threw unexpectedly.");
            return null;
        }
        catch (Exception ex)
        {
            // Broad fallback catch (invariant #17 audit 2026-06-12): any exception
            // type not in the typed list above must still return null rather than
            // crashing GetStatusAsync via Task.WhenAll propagation.
            _logger.LogWarning(ex,
                "SystemStatusProvider: Cosmos canary probe threw an unexpected exception type '{ExceptionType}'. Treating as unknown.",
                ex.GetType().Name);
            return null;
        }
    }
}
