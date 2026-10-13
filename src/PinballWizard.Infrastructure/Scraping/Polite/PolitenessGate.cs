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
/// <see cref="RobotsTxtCache"/> for per-host allow rules; tracks the
/// consecutive-429 streak and the rate-limit backoff per origin, so a
/// source that asks us to slow down is left alone for as long as it asked
/// (and a healthy origin elsewhere in the run neither resets nor inherits it).
/// </summary>
public sealed class PolitenessGate : IPolitenessGate
{
    private readonly RobotsTxtCache _robots;
    private readonly IPerSourcePolitenessResolver _resolver;
    private readonly ILogger<PolitenessGate> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, OriginThrottle> _origins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new gate.</summary>
    public PolitenessGate(
        RobotsTxtCache robots,
        IPerSourcePolitenessResolver resolver,
        ILogger<PolitenessGate> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(robots);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _robots = robots;
        _resolver = resolver;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Current consecutive-429 streak for the origin of <paramref name="url"/>. Exposed for diagnostics + tests.</summary>
    public int GetConsecutiveTooManyRequests(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return _origins.TryGetValue(OriginOf(url), out var throttle) ? throttle.TooManyRequestsStreak : 0;
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireForRequestAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        var effective = await _resolver.ResolveAsync(url, cancellationToken).ConfigureAwait(false);

        if (effective.RespectRobotsTxt)
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

        var throttle = _origins.GetOrAdd(OriginOf(url), _ => new OriginThrottle());

        await throttle.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WaitForDelayAsync(url, throttle, effective.RequestDelayMs, cancellationToken).ConfigureAwait(false);
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

        var throttle = _origins.GetOrAdd(OriginOf(url), _ => new OriginThrottle());

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            var effective = await _resolver.ResolveAsync(url, cancellationToken).ConfigureAwait(false);
            var streak = Interlocked.Increment(ref throttle.TooManyRequestsStreak);
            _logger.LogWarning("Received 429 from {Url} (streak={Streak}/{Max}). Retry-After={RetryAfter}.",
                url, streak, effective.Max429Streak, retryAfter);

            if (streak > effective.Max429Streak)
            {
                throw new PolitenessException(
                    PolitenessViolation.TooMany429Responses,
                    $"Source {url.Host} returned 429 {streak} times in a row (max allowed: {effective.Max429Streak}) " +
                    $"for {url}. Aborting rather than asking again; last Retry-After: {FormatRetryAfter(retryAfter)}.",
                    url);
            }

            var maxWait = TimeSpan.FromSeconds(effective.MaxRetryAfterSeconds);
            TimeSpan backoff;
            string basis;
            if (retryAfter is { } requested)
            {
                if (requested > maxWait)
                {
                    throw new PolitenessException(
                        PolitenessViolation.RetryAfterExceedsBudget,
                        $"Source {url.Host} asked us to wait {requested} (Retry-After) for {url}, longer than the " +
                        $"{maxWait} budget (MaxRetryAfterSeconds). Aborting rather than returning early.",
                        url);
                }
                backoff = requested;
                basis = "Retry-After";
            }
            else
            {
                var exponential = TimeSpan.FromMilliseconds(effective.RateLimitBackoffMs * Math.Pow(2, streak - 1));
                backoff = exponential < maxWait ? exponential : maxWait;
                basis = "policy (no Retry-After)";
            }

            var notBefore = _timeProvider.GetUtcNow() + backoff;
            throttle.ExtendNotBefore(notBefore);
            _logger.LogWarning(
                "Backing off {Origin} for {Backoff} per {Basis}; next request not before {NotBefore:O}.",
                OriginOf(url), backoff, basis, notBefore);
            return;
        }

        if ((int)statusCode is >= 200 and < 400)
        {
            Interlocked.Exchange(ref throttle.TooManyRequestsStreak, 0);
        }
        // Other 4xx / 5xx: leave streak unchanged. Caller decides whether to retry.
    }

    private async Task WaitForDelayAsync(Uri url, OriginThrottle throttle, int requestDelayMs, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var paceDue = throttle.LastRequestAt is { } last
            ? last + TimeSpan.FromMilliseconds(requestDelayMs)
            : now;
        var backoffDue = throttle.NotBefore;
        var due = backoffDue is { } b && b > paceDue ? b : paceDue;

        var remaining = due - now;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        if (backoffDue is { } bd && bd >= due)
        {
            _logger.LogInformation(
                "Honoring rate-limit backoff for {Origin}: waiting {Remaining} before requesting {Url}.",
                OriginOf(url), remaining, url);
        }

        await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static string OriginOf(Uri url) => url.GetLeftPart(UriPartial.Authority);

    private static string FormatRetryAfter(TimeSpan? retryAfter) =>
        retryAfter is { } r ? r.ToString() : "none sent";

    private sealed class OriginThrottle
    {
        private readonly Lock _gate = new();
        private DateTimeOffset? _notBefore;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTimeOffset? LastRequestAt { get; set; }

        // Field (not property) so Interlocked can operate on it.
        public int TooManyRequestsStreak;

        public DateTimeOffset? NotBefore
        {
            get { lock (_gate) return _notBefore; }
        }

        public void ExtendNotBefore(DateTimeOffset candidate)
        {
            lock (_gate)
            {
                if (_notBefore is null || candidate > _notBefore)
                {
                    _notBefore = candidate;
                }
            }
        }
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
