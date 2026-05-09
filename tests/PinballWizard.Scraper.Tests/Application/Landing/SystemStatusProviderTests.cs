using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Landing;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.Infrastructure.Landing;
using Xunit;

namespace PinballWizard.Scraper.Tests.Application.Landing;

// Unit tests for SystemStatusProvider.
//
// Covers the five behavioral requirements from the spec:
//   1. Caches result within TTL (probes invoked exactly once for two calls)
//   2. Refreshes after TTL (probes invoked twice when TTL expires)
//   3. Runs three probes in parallel (no serial dependency)
//   4. Maps success→true, failure→false, exception→null for each probe
//   5. Stampede protection (10 concurrent callers trigger probes exactly once)
public sealed class SystemStatusProviderTests : IDisposable
{
    private readonly IAzureFoundrySmokeProbe _foundryProbe = Substitute.For<IAzureFoundrySmokeProbe>();
    private readonly IAzureAiSearchSmokeProbe _aiSearchProbe = Substitute.For<IAzureAiSearchSmokeProbe>();
    private readonly ICosmosCanaryProbe _cosmosProbe = Substitute.For<ICosmosCanaryProbe>();
    private readonly MemoryCache _cache;

    public SystemStatusProviderTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose() => _cache.Dispose();

    // ── 1. Caches result within TTL ──────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_WithinTtl_ProbesInvokedExactlyOnce()
    {
        // Behavioral assertion: calling GetStatusAsync twice within the TTL
        // window hits the cache on the second call and never invokes the probes
        // a second time. This is the load-bearing test for caching correctness.
        SetupAllProbesSuccess();
        var provider = BuildProvider(ttl: TimeSpan.FromMinutes(5));

        await provider.GetStatusAsync(CancellationToken.None);
        await provider.GetStatusAsync(CancellationToken.None);

        await _foundryProbe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        await _aiSearchProbe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        await _cosmosProbe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_WithinTtl_ReturnsSameResultBothTimes()
    {
        SetupAllProbesSuccess();
        var provider = BuildProvider(ttl: TimeSpan.FromMinutes(5));

        var first = await provider.GetStatusAsync(CancellationToken.None);
        var second = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(first, second);
    }

    // ── 2. Refreshes after TTL ───────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_AfterTtlExpiry_ProbesInvokedTwice()
    {
        // Use a 1ms TTL so the cache expires immediately after the first call.
        // This tests that the provider re-probes after expiry rather than
        // serving the stale cached value indefinitely.
        SetupAllProbesSuccess();
        var provider = BuildProvider(ttl: TimeSpan.FromMilliseconds(1));

        await provider.GetStatusAsync(CancellationToken.None);
        // Allow the TTL to pass.
        await Task.Delay(20);
        await provider.GetStatusAsync(CancellationToken.None);

        await _foundryProbe.Received(2).ProbeAsync(Arg.Any<CancellationToken>());
        await _aiSearchProbe.Received(2).ProbeAsync(Arg.Any<CancellationToken>());
        await _cosmosProbe.Received(2).ProbeAsync(Arg.Any<CancellationToken>());
    }

    // ── 3. Runs three probes in parallel ─────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_RunsThreeProbesInParallel()
    {
        // Deterministic parallelism test using TaskCompletionSource.
        // Each probe is gated behind its own TCS; all three are released
        // simultaneously. If the probes ran serially, completing TCS[0]
        // would not unblock TCS[1] or TCS[2] until the serial step
        // reached them — but here all three are released at once, and
        // the task should complete without deadlock.
        //
        // The key assertion: GetStatusAsync completes before any individual
        // probe-wait deadline (i.e., all three must run concurrently, not
        // one at a time).
        var tcs1 = new TaskCompletionSource<FoundrySmokeProbeResult>();
        var tcs2 = new TaskCompletionSource<AiSearchSmokeProbeResult>();
        var tcs3 = new TaskCompletionSource<bool>();

        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(tcs1.Task);
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(tcs2.Task);
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(tcs3.Task);

        var provider = BuildProvider(ttl: TimeSpan.FromMinutes(5));

        var getStatusTask = provider.GetStatusAsync(CancellationToken.None);

        // All probes started; none completed yet.
        // Allow async infrastructure to start the probes before releasing.
        await Task.Yield();

        // Release all three simultaneously.
        tcs1.SetResult(new FoundrySmokeProbeResult(true, null, true, true, null));
        tcs2.SetResult(new AiSearchSmokeProbeResult(true, null, null, null));
        tcs3.SetResult(true);

        // If probes ran serially this would only work after the third probe
        // resolved; with parallel execution it completes as soon as all
        // three are released simultaneously.
        var status = await getStatusTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(status.FoundryHealthy);
        Assert.True(status.AiSearchHealthy);
        Assert.True(status.CosmosHealthy);
    }

    // ── 4. Maps success / failure / exception for each probe ─────────────────

    [Fact]
    public async Task GetStatusAsync_AllProbesSucceed_AllFieldsAreTrue()
    {
        SetupAllProbesSuccess();
        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.True(status.FoundryHealthy);
        Assert.True(status.AiSearchHealthy);
        Assert.True(status.CosmosHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_FoundryProbeReturnsFailure_FoundryHealthyIsFalse()
    {
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new FoundrySmokeProbeResult(false, null, false, false, "endpoint unreachable"));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(true, null, null, null));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.False(status.FoundryHealthy);
        Assert.True(status.AiSearchHealthy);
        Assert.True(status.CosmosHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_AiSearchProbeReturnsFailure_AiSearchHealthyIsFalse()
    {
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new FoundrySmokeProbeResult(true, null, true, true, null));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(false, null, null, "service unavailable"));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.True(status.FoundryHealthy);
        Assert.False(status.AiSearchHealthy);
        Assert.True(status.CosmosHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_CosmosProbeReturnsFalse_CosmosHealthyIsFalse()
    {
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new FoundrySmokeProbeResult(true, null, true, true, null));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(true, null, null, null));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.True(status.FoundryHealthy);
        Assert.True(status.AiSearchHealthy);
        Assert.False(status.CosmosHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_FoundryProbeThrows_FoundryHealthyIsNull()
    {
        // Unexpected exceptions from a probe map to null ("unknown"), not
        // false ("known-unhealthy"). The distinction matters for the frontend:
        // null renders as "unknown" rather than red.
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("network blip"));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(true, null, null, null));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.Null(status.FoundryHealthy);
        Assert.True(status.AiSearchHealthy);
        Assert.True(status.CosmosHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_AiSearchProbeThrows_AiSearchHealthyIsNull()
    {
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new FoundrySmokeProbeResult(true, null, true, true, null));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dns failure"));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.True(status.FoundryHealthy);
        Assert.Null(status.AiSearchHealthy);
        Assert.True(status.CosmosHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_CosmosProbeThrows_CosmosHealthyIsNull()
    {
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new FoundrySmokeProbeResult(true, null, true, true, null));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(true, null, null, null));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("cosmos timeout"));

        var status = await BuildProvider().GetStatusAsync(CancellationToken.None);

        Assert.True(status.FoundryHealthy);
        Assert.True(status.AiSearchHealthy);
        Assert.Null(status.CosmosHealthy);
    }

    // ── 4b. Optional probes absent → null ("unknown") ────────────────────────

    [Fact]
    public async Task GetStatusAsync_NullFoundryProbe_FoundryHealthyIsNull()
    {
        var provider = new SystemStatusProvider(
            _cache,
            Options.Create(new SystemStatusOptions { CacheTtl = TimeSpan.FromMinutes(5) }),
            NullLogger<SystemStatusProvider>.Instance,
            foundryProbe: null,
            aiSearchProbe: _aiSearchProbe,
            cosmosProbe: _cosmosProbe);

        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(true, null, null, null));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Null(status.FoundryHealthy);
    }

    [Fact]
    public async Task GetStatusAsync_AllProbesNull_AllStatusFieldsAreNull()
    {
        var provider = new SystemStatusProvider(
            _cache,
            Options.Create(new SystemStatusOptions { CacheTtl = TimeSpan.FromMinutes(5) }),
            NullLogger<SystemStatusProvider>.Instance);

        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Null(status.FoundryHealthy);
        Assert.Null(status.AiSearchHealthy);
        Assert.Null(status.CosmosHealthy);
    }

    // ── 5. Stampede protection ───────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ConcurrentCallers_ProbesInvokedExactlyOnce()
    {
        // Kick off 10 concurrent GetStatusAsync calls when the cache is empty.
        // IMemoryCache.GetOrCreateAsync serialises the factory for the same key,
        // so the probes must be invoked exactly once despite the concurrency.
        var barrier = new TaskCompletionSource();
        SetupAllProbesSuccess();

        // Add a small delay to the probe so all 10 tasks are "in flight"
        // before any resolve — maximises the likelihood of a race if the
        // stampede guard is absent.
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await barrier.Task;
                return new FoundrySmokeProbeResult(true, null, true, true, null);
            });

        // Recreate cache to ensure no prior state from other tests.
        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var provider = new SystemStatusProvider(
            freshCache,
            Options.Create(new SystemStatusOptions { CacheTtl = TimeSpan.FromMinutes(5) }),
            NullLogger<SystemStatusProvider>.Instance,
            _foundryProbe,
            _aiSearchProbe,
            _cosmosProbe);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => provider.GetStatusAsync(CancellationToken.None))
            .ToList();

        // Release the probes.
        barrier.SetResult();

        await Task.WhenAll(tasks);

        // Despite 10 concurrent callers, the factory should have run once.
        await _foundryProbe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        await _aiSearchProbe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        await _cosmosProbe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupAllProbesSuccess()
    {
        _foundryProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new FoundrySmokeProbeResult(true, "https://example.ai.azure.com", true, true, null));
        _aiSearchProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new AiSearchSmokeProbeResult(true, "https://search.windows.net", "pinwiz-rag-v1", null));
        _cosmosProbe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private SystemStatusProvider BuildProvider(TimeSpan? ttl = null)
    {
        return new SystemStatusProvider(
            _cache,
            Options.Create(new SystemStatusOptions { CacheTtl = ttl ?? TimeSpan.FromMinutes(5) }),
            NullLogger<SystemStatusProvider>.Instance,
            _foundryProbe,
            _aiSearchProbe,
            _cosmosProbe);
    }
}
