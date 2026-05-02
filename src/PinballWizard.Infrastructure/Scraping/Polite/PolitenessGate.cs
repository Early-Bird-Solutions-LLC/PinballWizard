using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Default implementation of <see cref="IPolitenessGate"/>. Maintains
/// per-origin throttle state in a process-wide
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>; consults
/// <see cref="RobotsTxtCache"/> for per-host allow rules; tracks a
/// process-wide 429 streak across all origins (we treat any
/// rate-limit response as a strong signal regardless of which origin
/// it came from).
/// </summary>
public sealed class PolitenessGate : IPolitenessGate
{
    private readonly RobotsTxtCache _robots;
    private readonly PolitenessOptions _options;
    private readonly ILogger<PolitenessGate> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, OriginThrottle> _origins = new(StringComparer.OrdinalIgnoreCase);

    private int _consecutiveTooManyRequests;

    /// <summary>Current consecutive-429 streak. Exposed for diagnostics + tests.</summary>
    public int ConsecutiveTooManyRequests => _consecutiveTooManyRequests;

    /// <summary>Initializes a new gate.</summary>
    public PolitenessGate(
        RobotsTxtCache robots,
        IOptions<PolitenessOptions> options,
        ILogger<PolitenessGate> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(robots);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _robots = robots;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireForRequestAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (_options.RespectRobotsTxt)
        {
            var allowed = await _robots.IsAllowedAsync(url, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                _logger.LogWarning("robots.txt disallows {Url} for our User-Agent — refusing the request.", url);
                throw new PolitenessException(
                    PolitenessViolation.RobotsTxtDisallow,
                    $"robots.txt disallows access to {url} for the configured User-Agent.",
                    url);
            }
        }

        var origin = url.GetLeftPart(UriPartial.Authority);
        var throttle = _origins.GetOrAdd(origin, _ => new OriginThrottle());

        await throttle.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WaitForDelayAsync(throttle, cancellationToken).ConfigureAwait(false);
            return new Lease(throttle, _timeProvider);
        }
        catch
        {
            throttle.Semaphore.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ReportResponseAsync(Uri url, HttpStatusCode statusCode, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            var streak = Interlocked.Increment(ref _consecutiveTooManyRequests);
            _logger.LogWarning("Received 429 from {Url} (streak={Streak}/{Max}). Retry-After={RetryAfter}.",
                url, streak, _options.Max429Streak, retryAfter);

            if (streak > _options.Max429Streak)
            {
                throw new PolitenessException(
                    PolitenessViolation.TooMany429Responses,
                    $"Source {url.Host} returned 429 {streak} times in a row (max allowed: {_options.Max429Streak}). Aborting.",
                    url);
            }

            if (retryAfter is { } wait)
            {
                await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if ((int)statusCode is >= 200 and < 400)
        {
            // Successful enough — reset the streak.
            Interlocked.Exchange(ref _consecutiveTooManyRequests, 0);
        }
        // Other 4xx / 5xx: leave streak unchanged. Caller decides whether to retry.
    }

    private async Task WaitForDelayAsync(OriginThrottle throttle, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(_options.RequestDelayMs);
        var lastRequestAt = throttle.LastRequestAt;
        if (lastRequestAt is null)
        {
            return;
        }

        var elapsed = _timeProvider.GetUtcNow() - lastRequestAt.Value;
        var remaining = delay - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OriginThrottle
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTimeOffset? LastRequestAt { get; set; }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly OriginThrottle _throttle;
        private readonly TimeProvider _timeProvider;
        private int _disposed;

        public Lease(OriginThrottle throttle, TimeProvider timeProvider)
        {
            _throttle = throttle;
            _timeProvider = timeProvider;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _throttle.LastRequestAt = _timeProvider.GetUtcNow();
                _throttle.Semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
