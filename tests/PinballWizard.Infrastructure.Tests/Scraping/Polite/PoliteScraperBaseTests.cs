using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Http;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Behavioral tests for <see cref="PoliteScraperBase"/>. Verifies that
/// the base class enforces the project's polite-scraping invariants:
/// every outbound request acquires a politeness lease before the HTTP
/// send, the response is reported back to the gate, and the lease is
/// released even when the request throws.
/// </summary>
public sealed class PoliteScraperBaseTests
{
    private static PolitenessOptions DefaultOptions => new()
    {
        UserAgent = "PinballWizard/test",
        RequestDelayMs = 0,
        Max429Streak = 3,
    };

    // Minimal concrete subclass that exposes the protected methods for testing.
    private sealed class TestScraper(IPolitenessGate gate) : PoliteScraperBase(
        gate,
        DefaultOptions,
        NullLogger<TestScraper>.Instance)
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpClient client,
            HttpRequestMessage request,
            CancellationToken ct) => SendPolitelyAsync(client, request, ct);

        public Task<string> GetStringAsync(HttpClient client, Uri url, CancellationToken ct)
            => GetStringPolitelyAsync(client, url, ct);
    }

    [Fact]
    public async Task SendPolitelyAsync_BeforeSend_AcquiresPolitenessLease()
    {
        // Arrange
        int acquireCallCount = 0;
        int httpSendCallCount = 0;

        var gate = Substitute.For<IPolitenessGate>();

        // Track call order — acquire must happen before the HTTP send
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                acquireCallCount++;
                return Task.FromResult<IAsyncDisposable>(new NoOpLease());
            });
        gate.ReportResponseAsync(Arg.Any<Uri>(), Arg.Any<HttpStatusCode>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new QueueingHttpMessageHandler();
        var url = new Uri("https://example.com/page");
        handler.Map(url.AbsoluteUri, _ =>
        {
            httpSendCallCount++;
            // Acquire must have already happened (acquire=1, send=1 in order)
            Assert.Equal(1, acquireCallCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new TestScraper(gate);
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Act
        using var response = await scraper.SendAsync(httpClient, request, CancellationToken.None);

        // Assert
        Assert.Equal(1, acquireCallCount);
        Assert.Equal(1, httpSendCallCount);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await gate.Received(1).AcquireForRequestAsync(url, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStringPolitelyAsync_HappyPath_ReturnsResponseBodyString()
    {
        // Arrange
        const string expectedBody = "pinball wizard response body";

        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new NoOpLease()));
        gate.ReportResponseAsync(Arg.Any<Uri>(), Arg.Any<HttpStatusCode>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var url = new Uri("https://sternpinball.com/manuals/");
        var handler = new QueueingHttpMessageHandler();
        handler.MapHtml(url.AbsoluteUri, expectedBody);

        using var httpClient = new HttpClient(handler);
        var scraper = new TestScraper(gate);

        // Act
        var result = await scraper.GetStringAsync(httpClient, url, CancellationToken.None);

        // Assert
        Assert.Equal(expectedBody, result);
    }

    [Fact]
    public async Task SendPolitelyAsync_WhenRequestThrows_ReleasesLeaseInFinallyBlock()
    {
        // Arrange
        var leaseDisposed = false;

        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new TrackingLease(() => leaseDisposed = true)));
        gate.ReportResponseAsync(Arg.Any<Uri>(), Arg.Any<HttpStatusCode>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // A handler that always throws to simulate a network error
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("network error"));

        using var httpClient = new HttpClient(handler);
        var scraper = new TestScraper(gate);
        var url = new Uri("https://example.com/throws");
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Act + Assert — the send must throw AND the lease must be released
        await Assert.ThrowsAsync<HttpRequestException>(
            () => scraper.SendAsync(httpClient, request, CancellationToken.None));

        Assert.True(leaseDisposed, "Politeness lease must be released in the finally block even when the HTTP request throws.");
    }

    [Fact]
    public async Task SendPolitelyAsync_AfterResponse_ReportsStatusCodeToGate()
    {
        // Arrange
        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new NoOpLease()));
        gate.ReportResponseAsync(Arg.Any<Uri>(), Arg.Any<HttpStatusCode>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var url = new Uri("https://example.com/resource");
        var handler = new QueueingHttpMessageHandler();
        handler.Map(url.AbsoluteUri, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body"),
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new TestScraper(gate);
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Act
        using var _ = await scraper.SendAsync(httpClient, request, CancellationToken.None);

        // Assert — gate must receive the response status
        await gate.Received(1).ReportResponseAsync(
            url,
            HttpStatusCode.OK,
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendPolitelyAsync_WhenReportThrows_DisposesResponseAndReleasesLease()
    {
        // A 429-streak abort throws from ReportResponseAsync after the
        // response exists. The caller never sees that response, so the
        // base must dispose it (ResponseHeadersRead would otherwise pin
        // the connection) and still release the politeness lease.
        var leaseDisposed = false;
        TrackingContent? content = null;

        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new TrackingLease(() => leaseDisposed = true)));
        gate.ReportResponseAsync(Arg.Any<Uri>(), Arg.Any<HttpStatusCode>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new PolitenessException(
                PolitenessViolation.TooMany429Responses,
                "429 streak"));

        var url = new Uri("https://example.com/streak");
        var handler = new QueueingHttpMessageHandler();
        handler.Map(url.AbsoluteUri, _ =>
        {
            content = new TrackingContent("body"u8.ToArray());
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content };
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new TestScraper(gate);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var ex = await Assert.ThrowsAsync<PolitenessException>(
            () => scraper.SendAsync(httpClient, request, CancellationToken.None));

        Assert.Equal(PolitenessViolation.TooMany429Responses, ex.Violation);
        Assert.True(leaseDisposed);
        Assert.NotNull(content);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task SendPolitelyAsync_429ThenOk_ReportsBothAndResendsThroughAFreshLease()
    {
        var gate = new FakePolitenessGate();
        var url = new Uri("https://example.com/limited");
        var handler = new QueueingHttpMessageHandler();
        var calls = 0;
        handler.Map(url.AbsoluteUri, _ => ++calls == 1
            ? RateLimited(retryAfterSeconds: 7)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        using var httpClient = GateOwnedClient(handler);
        var scraper = new TestScraper(gate);

        var body = await scraper.GetStringAsync(httpClient, url, CancellationToken.None);

        Assert.Equal("ok", body);
        Assert.Equal(2, handler.Requests.Count);
        // Each attempt acquired its own lease (so the gate could apply the backoff)
        // and every response — the 429 included — was reported.
        Assert.Equal(2, gate.Acquired.Count);
        Assert.Equal(2, gate.LeasesDisposed);
        Assert.Equal(
            new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.OK },
            gate.Reported.Select(r => r.Status));
        Assert.Equal(TimeSpan.FromSeconds(7), gate.Reported[0].RetryAfter);
    }

    [Fact]
    public async Task SendPolitelyAsync_429WithHttpDateRetryAfter_ReportsResolvedWait()
    {
        var gate = new FakePolitenessGate();
        var url = new Uri("https://example.com/limited");
        var handler = new QueueingHttpMessageHandler();
        var calls = 0;
        handler.Map(url.AbsoluteUri, _ =>
        {
            if (++calls > 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Date", "Sun, 27 Sep 2026 11:00:00 GMT");
            response.Headers.TryAddWithoutValidation("Retry-After", "Sun, 27 Sep 2026 11:02:00 GMT");
            return response;
        });

        using var httpClient = GateOwnedClient(handler);
        await new TestScraper(gate).GetStringAsync(httpClient, url, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(2), gate.Reported[0].RetryAfter);
    }

    [Fact]
    public async Task SendPolitelyAsync_BotChallenge_ThrowsWithoutRetrying()
    {
        var gate = new FakePolitenessGate();
        var url = new Uri("https://www.kineticist.com/news/category/pinball-tutorial");
        var handler = new QueueingHttpMessageHandler();
        handler.Map(url.AbsoluteUri, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("<title>Vercel Security Checkpoint</title>"),
            };
            response.Headers.TryAddWithoutValidation("x-vercel-mitigated", "challenge");
            return response;
        });

        using var httpClient = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<PolitenessException>(
            () => new TestScraper(gate).GetStringAsync(httpClient, url, CancellationToken.None));

        Assert.Equal(PolitenessViolation.BotChallenge, ex.Violation);
        Assert.Equal(url, ex.Url);
        Assert.Contains("x-vercel-mitigated", ex.Message, StringComparison.Ordinal);
        // One request, reported to the gate, lease released — no second knock.
        Assert.Single(handler.Requests);
        Assert.Single(gate.Reported);
        Assert.Equal(HttpStatusCode.TooManyRequests, gate.Reported[0].Status);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task SendPolitelyAsync_429OnRequestWithBody_IsReturnedNotResent()
    {
        var gate = new FakePolitenessGate();
        var url = new Uri("https://example.com/search");
        var handler = new QueueingHttpMessageHandler();
        handler.Map(url.AbsoluteUri, _ => RateLimited(retryAfterSeconds: 5));

        using var httpClient = GateOwnedClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") };

        using var response = await new TestScraper(gate).SendAsync(httpClient, request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Single(gate.Reported);
    }

    [Fact]
    public async Task SendPolitelyAsync_Persistent429_StopsWhenTheGateSpendsTheBudget()
    {
        var gate = new FakePolitenessGate { ThrowOnReportAfter429s = 3 };
        var url = new Uri("https://example.com/limited");
        var handler = new QueueingHttpMessageHandler();
        handler.Map(url.AbsoluteUri, _ => RateLimited(retryAfterSeconds: null));

        using var httpClient = GateOwnedClient(handler);

        var ex = await Assert.ThrowsAsync<PolitenessException>(
            () => new TestScraper(gate).GetStringAsync(httpClient, url, CancellationToken.None));

        Assert.Equal(PolitenessViolation.TooMany429Responses, ex.Violation);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, gate.LeasesDisposed);
    }

    [Fact]
    public async Task SendPolitelyAsync_429OnPipelineThatMayRetryBelowTheGate_IsReturnedNotResent()
    {
        // No GateOwnsRateLimit stamp: the client may be on the host pipeline,
        // which already retried the 429. Re-sending on top would multiply requests.
        var gate = new FakePolitenessGate();
        var url = new Uri("https://example.com/limited");
        var handler = new QueueingHttpMessageHandler();
        handler.Map(url.AbsoluteUri, _ => RateLimited(retryAfterSeconds: 5));

        using var httpClient = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await new TestScraper(gate).SendAsync(httpClient, request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(handler.Requests);
        // Still reported, so the gate records the origin's backoff.
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(gate.Reported).RetryAfter);
    }

    private static HttpClient GateOwnedClient(HttpMessageHandler inner) =>
        new(new GateOwnsRateLimitHandler { InnerHandler = inner });

    private static HttpResponseMessage RateLimited(int? retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
        if (retryAfterSeconds is { } s)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", s.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return response;
    }

    // --- helpers ---

    private sealed class NoOpLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingLease(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingHttpMessageHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(ex);
    }
}
