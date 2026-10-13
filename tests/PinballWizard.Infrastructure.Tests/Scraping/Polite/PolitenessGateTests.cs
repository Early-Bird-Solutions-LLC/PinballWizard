using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Tests for <see cref="PolitenessGate"/>. Verifies the four invariants:
/// per-origin throttle, per-origin minimum delay, robots.txt enforcement,
/// 429-streak abort.
/// </summary>
public sealed class PolitenessGateTests
{
    private static PolitenessOptions DefaultOptions => new()
    {
        UserAgent = "PinballWizard/test",
        RequestDelayMs = 250,
        Max429Streak = 3,
        RespectRobotsTxt = false, // tests focused on throttle / 429 default to disabled
    };

    private static RobotsTxtCache CreateRobotsCache(string? robotsBody = null) =>
        new(new HttpClient(new StubRobotsHandler(robotsBody)), Options.Create(DefaultOptions), NullLogger<RobotsTxtCache>.Instance);

    private static PolitenessGate CreateGate(
        PolitenessOptions? options = null,
        RobotsTxtCache? robots = null,
        TimeProvider? timeProvider = null)
    {
        var opts = options ?? DefaultOptions;
        var resolver = new DefaultPerSourcePolitenessResolver(Options.Create(opts));
        return new PolitenessGate(
            robots ?? CreateRobotsCache(),
            resolver,
            NullLogger<PolitenessGate>.Instance,
            timeProvider);
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcquireForRequestAsync_FirstAcquire_DoesNotDelay()
    {
        var gate = CreateGate();
        var url = new Uri("https://example.com/page");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var lease = await gate.AcquireForRequestAsync(url, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"First acquire should not delay; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcquireForRequestAsync_TwoSequentialSameOrigin_AppliesDelay()
    {
        var options = DefaultOptions;
        options.RequestDelayMs = 250;
        var gate = CreateGate(options);
        var url = new Uri("https://example.com/page");

        await using (await gate.AcquireForRequestAsync(url, CancellationToken.None))
        {
            // Hold briefly to simulate request work.
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var second = await gate.AcquireForRequestAsync(url, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 200, $"Second acquire should be delayed >= 200ms; was {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcquireForRequestAsync_DifferentOrigins_NoDelayBetween()
    {
        var gate = CreateGate();

        await using (await gate.AcquireForRequestAsync(new Uri("https://a.example.com/x"), CancellationToken.None))
        {
            // first lease open
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var second = await gate.AcquireForRequestAsync(new Uri("https://b.example.com/x"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"Different origin should not delay; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcquireForRequestAsync_ConcurrentSameOrigin_Serializes()
    {
        var options = DefaultOptions;
        options.RequestDelayMs = 250;
        var gate = CreateGate(options);
        var url = new Uri("https://example.com/page");

        // Start two concurrent acquires; the second should not complete until
        // the first releases AND the delay has elapsed.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var firstTask = gate.AcquireForRequestAsync(url, CancellationToken.None);
        var secondTask = gate.AcquireForRequestAsync(url, CancellationToken.None);

        var first = await firstTask;
        await Task.Delay(50); // hold the first briefly
        await first.DisposeAsync();

        var second = await secondTask;
        sw.Stop();
        await second.DisposeAsync();

        Assert.True(sw.ElapsedMilliseconds >= 250, $"Second acquire should wait at least the configured delay; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcquireForRequestAsync_RobotsDisallow_Throws()
    {
        var options = DefaultOptions;
        options.RespectRobotsTxt = true;

        var robotsBody = """
            User-agent: *
            Disallow: /private/
            """;
        var robots = CreateRobotsCache(robotsBody);
        var gate = CreateGate(options, robots);

        var ex = await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await using var _ = await gate.AcquireForRequestAsync(new Uri("https://example.com/private/secret"), CancellationToken.None);
        });

        Assert.Equal(PolitenessViolation.RobotsTxtDisallow, ex.Violation);
    }

    [Fact]
    public async Task AcquireForRequestAsync_RobotsDisabled_DoesNotCheck()
    {
        var options = DefaultOptions;
        options.RespectRobotsTxt = false;

        var robotsBody = """
            User-agent: *
            Disallow: /
            """;
        var robots = CreateRobotsCache(robotsBody);
        var gate = CreateGate(options, robots);

        // Should not throw — robots disabled.
        await using var _ = await gate.AcquireForRequestAsync(new Uri("https://example.com/anything"), CancellationToken.None);
    }

    [Fact]
    public async Task ReportResponseAsync_Status200_ResetsStreak()
    {
        var gate = CreateGate();
        var url = new Uri("https://example.com/x");

        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        Assert.Equal(2, gate.GetConsecutiveTooManyRequests(url));

        await gate.ReportResponseAsync(url, HttpStatusCode.OK, retryAfter: null, CancellationToken.None);

        Assert.Equal(0, gate.GetConsecutiveTooManyRequests(url));
    }

    [Fact]
    public async Task ReportResponseAsync_429StreakExceeded_Throws()
    {
        var options = DefaultOptions;
        options.Max429Streak = 2;
        var gate = CreateGate(options);
        var url = new Uri("https://example.com/x");

        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<PolitenessException>(() =>
            gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None));

        Assert.Equal(PolitenessViolation.TooMany429Responses, ex.Violation);
    }

    [Fact]
    public async Task ReportResponseAsync_4xxNon429_DoesNotChangeStreak()
    {
        var gate = CreateGate();
        var url = new Uri("https://example.com/x");

        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        Assert.Equal(1, gate.GetConsecutiveTooManyRequests(url));

        // 404 is not a rate-limit signal — streak unchanged.
        await gate.ReportResponseAsync(url, HttpStatusCode.NotFound, retryAfter: null, CancellationToken.None);
        Assert.Equal(1, gate.GetConsecutiveTooManyRequests(url));
    }

    [Fact]
    public async Task ReportResponseAsync_429WithRetryAfter_NextAcquireForOriginWaitsThatLong()
    {
        var time = new AutoAdvancingTimeProvider(T0);
        var gate = CreateGate(timeProvider: time);
        var url = new Uri("https://example.com/x");

        await using (await gate.AcquireForRequestAsync(url, CancellationToken.None))
        {
            await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7), CancellationToken.None);
        }

        await using (await gate.AcquireForRequestAsync(url, CancellationToken.None))
        {
        }

        // The Retry-After (7 s) dominates the 250 ms pacing delay, and the report
        // itself did not sleep — the wait belongs to the next request.
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, time.Waits);
        Assert.Equal(T0 + TimeSpan.FromSeconds(7), time.GetUtcNow());
    }

    [Fact]
    public async Task ReportResponseAsync_429WithoutRetryAfter_AppliesDoublingPolicyBackoff()
    {
        var options = DefaultOptions;
        options.RateLimitBackoffMs = 30_000;
        options.Max429Streak = 3;
        var time = new AutoAdvancingTimeProvider(T0);
        var gate = CreateGate(options, timeProvider: time);
        var url = new Uri("https://example.com/x");

        for (var i = 0; i < 3; i++)
        {
            await using (await gate.AcquireForRequestAsync(url, CancellationToken.None))
            {
                await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
            }
        }
        await using (await gate.AcquireForRequestAsync(url, CancellationToken.None))
        {
        }

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) },
            time.Waits);
    }

    [Fact]
    public async Task ReportResponseAsync_PolicyBackoff_IsCappedAtMaxRetryAfter()
    {
        var options = DefaultOptions;
        options.RateLimitBackoffMs = 60_000;
        options.MaxRetryAfterSeconds = 90;
        var time = new AutoAdvancingTimeProvider(T0);
        var gate = CreateGate(options, timeProvider: time);
        var url = new Uri("https://example.com/x");

        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        await gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        await using (await gate.AcquireForRequestAsync(url, CancellationToken.None))
        {
        }

        // 60 s then 120 s requested; 120 s is capped to the 90 s budget.
        Assert.Equal(new[] { TimeSpan.FromSeconds(90) }, time.Waits);
    }

    [Fact]
    public async Task ReportResponseAsync_RetryAfterBeyondBudget_ThrowsInsteadOfSleeping()
    {
        var options = DefaultOptions;
        options.MaxRetryAfterSeconds = 600;
        var time = new AutoAdvancingTimeProvider(T0);
        var gate = CreateGate(options, timeProvider: time);
        var url = new Uri("https://example.com/x");

        var ex = await Assert.ThrowsAsync<PolitenessException>(() =>
            gate.ReportResponseAsync(url, HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2), CancellationToken.None));

        Assert.Equal(PolitenessViolation.RetryAfterExceedsBudget, ex.Violation);
        Assert.Contains("example.com", ex.Message, StringComparison.Ordinal);
        Assert.Empty(time.Waits);
    }

    [Fact]
    public async Task ReportResponseAsync_429Streak_IsTrackedPerOrigin()
    {
        var options = DefaultOptions;
        options.Max429Streak = 1;
        var gate = CreateGate(options, timeProvider: new AutoAdvancingTimeProvider(T0));
        var limited = new Uri("https://limited.example.com/x");
        var healthy = new Uri("https://healthy.example.com/x");

        await gate.ReportResponseAsync(limited, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);

        // A success on another origin neither resets the limited origin's streak
        // nor is the healthy origin charged for the limited one's 429.
        await gate.ReportResponseAsync(healthy, HttpStatusCode.OK, retryAfter: null, CancellationToken.None);
        Assert.Equal(1, gate.GetConsecutiveTooManyRequests(limited));
        Assert.Equal(0, gate.GetConsecutiveTooManyRequests(healthy));

        await gate.ReportResponseAsync(healthy, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PolitenessException>(() =>
            gate.ReportResponseAsync(limited, HttpStatusCode.TooManyRequests, retryAfter: null, CancellationToken.None));
        Assert.Equal(PolitenessViolation.TooMany429Responses, ex.Violation);
    }

    [Fact]
    public async Task ReportResponseAsync_429Backoff_DoesNotDelayOtherOrigins()
    {
        var time = new AutoAdvancingTimeProvider(T0);
        var gate = CreateGate(timeProvider: time);

        await gate.ReportResponseAsync(new Uri("https://limited.example.com/x"), HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(45), CancellationToken.None);
        await using (await gate.AcquireForRequestAsync(new Uri("https://other.example.com/x"), CancellationToken.None))
        {
        }

        Assert.Empty(time.Waits);
    }

    [Fact]
    public async Task AcquireForRequestAsync_NullUrl_Throws()
    {
        var gate = CreateGate();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await using var _ = await gate.AcquireForRequestAsync(null!, CancellationToken.None);
        });
    }

    private sealed class StubRobotsHandler : HttpMessageHandler
    {
        private readonly string? _robotsBody;

        public StubRobotsHandler(string? robotsBody)
        {
            _robotsBody = robotsBody;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_robotsBody is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_robotsBody, Encoding.UTF8, "text/plain"),
            });
        }
    }
}
