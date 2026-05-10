using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.Polite;

/// <summary>
/// Tests for <see cref="RobotsTxtCache"/>. Verifies fetch behavior,
/// caching, TTL refresh, and the permissive fallback when robots.txt is
/// missing or unfetchable.
/// </summary>
public sealed class RobotsTxtCacheTests : IDisposable
{
    private readonly StubHttpMessageHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private RobotsTxtCache CreateCache(int ttlSeconds = 3600)
    {
        var httpClient = new HttpClient(_handler);
        var options = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard/test",
            RequestDelayMs = 250,
            RobotsTxtPath = "/robots.txt",
            RobotsTxtTtlSeconds = ttlSeconds,
        });
        return new RobotsTxtCache(httpClient, options, NullLogger<RobotsTxtCache>.Instance);
    }

    [Fact]
    public async Task IsAllowedAsync_RobotsTxtDisallow_ReturnsFalse()
    {
        _handler.SetResponse("https://example.com/robots.txt", HttpStatusCode.OK, """
            User-agent: *
            Disallow: /private/
            """);

        var cache = CreateCache();

        var result = await cache.IsAllowedAsync(new Uri("https://example.com/private/page"), CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task IsAllowedAsync_RobotsTxt404_TreatsAsAllowed()
    {
        _handler.SetResponse("https://example.com/robots.txt", HttpStatusCode.NotFound, "");

        var cache = CreateCache();

        var result = await cache.IsAllowedAsync(new Uri("https://example.com/anything"), CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task IsAllowedAsync_NetworkFailure_TreatsAsAllowed()
    {
        _handler.SetException(new HttpRequestException("network down"));

        var cache = CreateCache();

        var result = await cache.IsAllowedAsync(new Uri("https://example.com/anything"), CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task IsAllowedAsync_SecondCallSameHost_HitsCache()
    {
        _handler.SetResponse("https://example.com/robots.txt", HttpStatusCode.OK, """
            User-agent: *
            Disallow: /private/
            """);

        var cache = CreateCache();

        await cache.IsAllowedAsync(new Uri("https://example.com/page1"), CancellationToken.None);
        await cache.IsAllowedAsync(new Uri("https://example.com/page2"), CancellationToken.None);
        await cache.IsAllowedAsync(new Uri("https://example.com/page3"), CancellationToken.None);

        Assert.Equal(1, _handler.RequestCount);
    }

    [Fact]
    public async Task IsAllowedAsync_DifferentHosts_FetchesEach()
    {
        _handler.SetResponse("https://a.example.com/robots.txt", HttpStatusCode.OK, "User-agent: *\nDisallow:");
        _handler.SetResponse("https://b.example.com/robots.txt", HttpStatusCode.OK, "User-agent: *\nDisallow:");

        var cache = CreateCache();

        await cache.IsAllowedAsync(new Uri("https://a.example.com/x"), CancellationToken.None);
        await cache.IsAllowedAsync(new Uri("https://b.example.com/x"), CancellationToken.None);

        Assert.Equal(2, _handler.RequestCount);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);
        private Exception? _exception;

        public int RequestCount { get; private set; }

        public void SetResponse(string url, HttpStatusCode status, string body)
        {
            _responses[url] = (status, body);
        }

        public void SetException(Exception exception)
        {
            _exception = exception;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (_exception is not null)
            {
                return Task.FromException<HttpResponseMessage>(_exception);
            }

            var url = request.RequestUri!.ToString();
            if (_responses.TryGetValue(url, out var entry))
            {
                var response = new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "text/plain"),
                };
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
