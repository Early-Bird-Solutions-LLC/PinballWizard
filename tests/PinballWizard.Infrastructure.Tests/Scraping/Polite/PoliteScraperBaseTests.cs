using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Core.Configuration;
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
        var requestedUrls = new List<Uri>();
        int acquireCallCount = 0;
        int httpSendCallCount = 0;

        var gate = Substitute.For<IPolitenessGate>();

        // Track call order — acquire must happen before the HTTP send
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                requestedUrls.Add(callInfo.Arg<Uri>());
                acquireCallCount = ++acquireCallCount; // captured in closure
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

    private sealed class ThrowingHttpMessageHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(ex);
    }
}
