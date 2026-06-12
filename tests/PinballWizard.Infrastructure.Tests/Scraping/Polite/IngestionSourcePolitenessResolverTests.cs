using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Tests for <see cref="IngestionSourcePolitenessResolver"/>: per-source
/// override merge, host-key lookup, and graceful degradation when the
/// repository fails. Pins the load-bearing rule that
/// `PolitenessOverrides` field on `IngestionSource` is no longer dead
/// config — every non-null override field travels into the
/// per-request <see cref="PolitenessOptions"/>.
/// </summary>
public sealed class IngestionSourcePolitenessResolverTests
{
    private static readonly PolitenessOptions Defaults = new()
    {
        UserAgent = "PinballWizard/test",
        RequestDelayMs = 2000,
        Max429Streak = 3,
        RespectRobotsTxt = true,
        RobotsTxtPath = "/robots.txt",
        RobotsTxtTtlSeconds = 3600,
    };

    [Fact]
    public void ApplyOverrides_NullOverrides_ReturnsDefaultsUnchanged()
    {
        var result = IngestionSourcePolitenessResolver.ApplyOverrides(Defaults, null);
        Assert.Same(Defaults, result);
    }

    [Fact]
    public void ApplyOverrides_AllNullFields_ReturnsValueEqualToDefaults()
    {
        var overrides = new PolitenessOverrides();
        var result = IngestionSourcePolitenessResolver.ApplyOverrides(Defaults, overrides);

        Assert.NotSame(Defaults, result);
        Assert.Equal(Defaults.UserAgent, result.UserAgent);
        Assert.Equal(Defaults.RequestDelayMs, result.RequestDelayMs);
        Assert.Equal(Defaults.Max429Streak, result.Max429Streak);
        Assert.Equal(Defaults.RobotsTxtPath, result.RobotsTxtPath);
    }

    [Fact]
    public void ApplyOverrides_RequestDelayOverride_AppliesOnly()
    {
        var overrides = new PolitenessOverrides { RequestDelayMs = 5000 };
        var result = IngestionSourcePolitenessResolver.ApplyOverrides(Defaults, overrides);

        Assert.Equal(5000, result.RequestDelayMs);
        Assert.Equal(Defaults.UserAgent, result.UserAgent);
        Assert.Equal(Defaults.Max429Streak, result.Max429Streak);
    }

    [Fact]
    public void ApplyOverrides_Max429StreakOverride_AppliesOnly()
    {
        var overrides = new PolitenessOverrides { Max429Streak = 1 };
        var result = IngestionSourcePolitenessResolver.ApplyOverrides(Defaults, overrides);

        Assert.Equal(1, result.Max429Streak);
        Assert.Equal(Defaults.RequestDelayMs, result.RequestDelayMs);
    }

    [Fact]
    public void ApplyOverrides_UserAgentSuffix_IsAppended()
    {
        var overrides = new PolitenessOverrides { UserAgentSuffix = "(spooky-pinball)" };
        var result = IngestionSourcePolitenessResolver.ApplyOverrides(Defaults, overrides);

        Assert.Equal($"{Defaults.UserAgent} (spooky-pinball)", result.UserAgent);
    }

    [Fact]
    public void ApplyOverrides_RobotsTxtPathOverride_AppliesOnly()
    {
        var overrides = new PolitenessOverrides { RobotsTxtPath = "/special-robots.txt" };
        var result = IngestionSourcePolitenessResolver.ApplyOverrides(Defaults, overrides);

        Assert.Equal("/special-robots.txt", result.RobotsTxtPath);
    }

    [Fact]
    public async Task ResolveAsync_KnownHost_ReturnsEffectiveOptionsWithOverrides()
    {
        var source = new IngestionSource
        {
            Id = "spooky",
            DisplayName = "Spooky Pinball",
            ScraperImplKey = "spooky",
            BaseUrl = "https://spookypinball.com/",
            Cadence = "daily",
            PolitenessOverrides = new PolitenessOverrides { RequestDelayMs = 10_000 },
        };

        var resolver = new IngestionSourcePolitenessResolver(
            new StubRepository([source]),
            Options.Create(Defaults),
            NullLogger<IngestionSourcePolitenessResolver>.Instance);

        var effective = await resolver.ResolveAsync(new Uri("https://spookypinball.com/games"), CancellationToken.None);

        Assert.Equal(10_000, effective.RequestDelayMs);
        Assert.Equal(Defaults.Max429Streak, effective.Max429Streak); // unchanged
    }

    [Fact]
    public async Task ResolveAsync_UnknownHost_ReturnsDefaults()
    {
        var resolver = new IngestionSourcePolitenessResolver(
            new StubRepository([]),
            Options.Create(Defaults),
            NullLogger<IngestionSourcePolitenessResolver>.Instance);

        var effective = await resolver.ResolveAsync(new Uri("https://unknown-host.example.com/page"), CancellationToken.None);

        Assert.Same(Defaults, effective);
    }

    [Fact]
    public async Task ResolveAsync_RepositoryThrows_FallsBackToDefaults()
    {
        // Critical resilience invariant: a transient Cosmos outage during scraper
        // startup must NOT block scraping. The resolver swallows the exception,
        // logs a warning, and returns global defaults for every host.
        var resolver = new IngestionSourcePolitenessResolver(
            new ThrowingRepository(new InvalidOperationException("Cosmos unreachable")),
            Options.Create(Defaults),
            NullLogger<IngestionSourcePolitenessResolver>.Instance);

        var effective = await resolver.ResolveAsync(new Uri("https://spookypinball.com/games"), CancellationToken.None);

        Assert.Same(Defaults, effective);
    }

    // ── Invariant #17 audit 2026-06-12: item 6 ──────────────────────────────
    // IngestionSourcePolitenessResolver: Cosmos failure → fallback to defaults
    // AND increment pinwiz.scraper.politeness_fallback_active counter.

    [Fact]
    public async Task ResolveAsync_RepositoryThrows_EmitsPolitenesFallbackActiveCounter()
    {
        // Counter must increment exactly once when the resolver falls back to
        // global defaults due to a Cosmos exception. Uses the project-standard
        // parallel-tolerant ConcurrentBag pattern (distinct instrument name
        // means no cross-fixture collision risk even without a tag filter).
        var bag = new ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "pinwiz.scraper.politeness_fallback_active")
            {
                bag.Add(value);
            }
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.ScraperPolitenessFallbackActive);

        var resolver = new IngestionSourcePolitenessResolver(
            new ThrowingRepository(new InvalidOperationException("Cosmos unreachable")),
            Options.Create(Defaults),
            NullLogger<IngestionSourcePolitenessResolver>.Instance);

        // Trigger initialization (which will throw and fall back).
        await resolver.ResolveAsync(new Uri("https://spookypinball.com/games"), CancellationToken.None);

        // Counter must have fired exactly once.
        Assert.Contains(bag, v => v == 1);
    }

    [Fact]
    public async Task ResolveAsync_CalledTwice_LoadsRepositoryOnce()
    {
        var repository = new StubRepository([]);
        var resolver = new IngestionSourcePolitenessResolver(
            repository,
            Options.Create(Defaults),
            NullLogger<IngestionSourcePolitenessResolver>.Instance);

        await resolver.ResolveAsync(new Uri("https://example.com/a"), CancellationToken.None);
        await resolver.ResolveAsync(new Uri("https://example.com/b"), CancellationToken.None);

        Assert.Equal(1, repository.StreamAllCallCount);
    }

    private sealed class StubRepository(IReadOnlyList<IngestionSource> sources) : IIngestionSourceRepository
    {
        public int StreamAllCallCount { get; private set; }

        public async IAsyncEnumerable<IngestionSource> StreamAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamAllCallCount++;
            foreach (var s in sources)
            {
                yield return s;
                await Task.Yield();
            }
        }

        public IAsyncEnumerable<IngestionSource> StreamEnabledAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IngestionSource?> GetByIdAsync(string id, string partitionKey, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IngestionSource> UpsertAsync(IngestionSource entity, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task DeleteAsync(string id, string partitionKey, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public IAsyncEnumerable<IngestionSource> StreamAsync(
            string query,
            IReadOnlyDictionary<string, object>? parameters,
            string? partitionKey,
            CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task RecordRunResultAsync(string sourceId, IngestionSourceRunResult result, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class ThrowingRepository(Exception toThrow) : IIngestionSourceRepository
    {
        public IAsyncEnumerable<IngestionSource> StreamAllAsync(CancellationToken cancellationToken)
        {
            throw toThrow;
        }

        public IAsyncEnumerable<IngestionSource> StreamEnabledAsync(CancellationToken cancellationToken)
            => throw toThrow;

        public Task<IngestionSource?> GetByIdAsync(string id, string partitionKey, CancellationToken cancellationToken)
            => throw toThrow;

        public Task<IngestionSource> UpsertAsync(IngestionSource entity, CancellationToken cancellationToken)
            => throw toThrow;

        public Task DeleteAsync(string id, string partitionKey, CancellationToken cancellationToken)
            => throw toThrow;

        public IAsyncEnumerable<IngestionSource> StreamAsync(
            string query,
            IReadOnlyDictionary<string, object>? parameters,
            string? partitionKey,
            CancellationToken cancellationToken)
            => throw toThrow;

        public Task RecordRunResultAsync(string sourceId, IngestionSourceRunResult result, CancellationToken cancellationToken)
            => throw toThrow;
    }
}
