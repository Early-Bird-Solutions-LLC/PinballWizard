using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Ingestion;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

// Behavior tests for HttpDocumentBytesSource. Pins the SSRF hardening
// guard (non-http/non-https schemes throw), the http→https legacy-record
// upgrade, the politeness-gate acquire that used to be skipped, and the
// response-buffering contract (returns a seekable stream PdfPig can use).
// A 403 is a thrown failure — the source does not return the error body
// or an empty stand-in stream.
public sealed class HttpDocumentBytesSourceTests
{
    [Fact]
    public async Task OpenAsync_HttpsUrl_AcquiresGateBeforeSend_AndReturnsSeekableStream()
    {
        var bytes = "fake-pdf-bytes"u8.ToArray();
        var gate = new FakePolitenessGate();
        var acquiredBeforeSend = false;
        var handler = new CallbackHttpHandler((request, _) =>
        {
            acquiredBeforeSend = gate.Acquired.Count == 1
                && gate.Acquired[0] == request.RequestUri;
            return Ok(bytes);
        });
        var source = NewSource(handler, gate);

        await using var stream = await source.OpenAsync(
            "https://example.com/doc.pdf", CancellationToken.None);

        Assert.True(acquiredBeforeSend, "IPolitenessGate must be acquired before the HTTP send.");
        Assert.Equal(new Uri("https://example.com/doc.pdf"), Assert.Single(gate.Acquired));
        Assert.Equal(HttpStatusCode.OK, Assert.Single(gate.Reported).Status);
        Assert.Equal(1, gate.LeasesDisposed);
        Assert.True(stream.CanSeek);
        Assert.Equal(0, stream.Position);
        var buffer = new byte[bytes.Length];
        var read = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(bytes.Length, read);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public async Task OpenAsync_HttpUrl_IsUpgradedToHttps_AndGateAcquiresUpgradedUrl()
    {
        // Legacy Cosmos records captured http:// before the scraper enforced
        // https. The source must silently upgrade and fetch — NOT throw.
        // The gate and the wire both see the rewritten https:// URL.
        var bytes = "legacy-pdf-bytes"u8.ToArray();
        var gate = new FakePolitenessGate();
        var handler = new CapturingHttpHandler(bytes);
        var source = new HttpDocumentBytesSource(
            new HttpClient(handler),
            gate,
            Options.Create(new PolitenessOptions()),
            NullLogger<HttpDocumentBytesSource>.Instance);

        await using var stream = await source.OpenAsync(
            "http://s4.american-pinball.com/img/support/manual.pdf", CancellationToken.None);

        const string upgraded = "https://s4.american-pinball.com/img/support/manual.pdf";
        Assert.True(stream.CanSeek);
        Assert.Equal(upgraded, handler.LastRequestUri?.ToString());
        Assert.Equal(new Uri(upgraded), Assert.Single(gate.Acquired));
        Assert.Equal(HttpStatusCode.OK, Assert.Single(gate.Reported).Status);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task OpenAsync_Forbidden_AcquiresGate_Reports403_AndThrows()
    {
        // Manufacturer sites that refuse Azure egress return 403. The fetch
        // still goes through the gate, then the status is reported and thrown.
        // The error body must not come back as document bytes.
        var gate = new FakePolitenessGate();
        var handler = new StatusHttpHandler(HttpStatusCode.Forbidden, "blocked-by-waf"u8.ToArray());
        var source = NewSource(handler, gate);
        const string url = "https://spookypinball.com/wp-content/uploads/Halloween-and-Ultraman-Manual.pdf";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.OpenAsync(url, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(new Uri(url), Assert.Single(gate.Acquired));
        Assert.Equal(HttpStatusCode.Forbidden, Assert.Single(gate.Reported).Status);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task OpenAsync_WhenGateRefuses_DoesNotSend_AndPropagates()
    {
        // A robots.txt disallow (or 429-streak abort) is a real stop. The
        // HTTP client must not run, and the refusal must not be rewritten
        // into an empty document stream.
        var url = new Uri("https://example.com/private/manual.pdf");
        var gate = new FakePolitenessGate
        {
            ThrowOnAcquire = new PolitenessException(
                PolitenessViolation.RobotsTxtDisallow,
                "robots.txt disallows access",
                url),
        };
        var handler = new StatusHttpHandler(HttpStatusCode.OK, "should-not-be-read"u8.ToArray());
        var source = NewSource(handler, gate);

        var ex = await Assert.ThrowsAsync<PolitenessException>(() =>
            source.OpenAsync(url.AbsoluteUri, CancellationToken.None));

        Assert.Equal(PolitenessViolation.RobotsTxtDisallow, ex.Violation);
        Assert.Equal(0, handler.SendCount);
        Assert.Empty(gate.Acquired);
        Assert.Empty(gate.Reported);
    }

    [Theory]
    [InlineData("ftp://example.com/doc.pdf")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    [InlineData("//relative.example/doc.pdf")]
    public async Task OpenAsync_NonHttpAndNonHttpsUrl_ThrowsArgumentException_WithoutAcquiring(string documentUrl)
    {
        // SSRF hardening: http:// is upgraded to https:// upstream; all other
        // non-https schemes (ftp://, file://, etc.) indicate source-data
        // corruption or a poisoned change-feed payload and are rejected
        // before the politeness gate or the wire.
        var gate = new FakePolitenessGate();
        var handler = new StatusHttpHandler(HttpStatusCode.OK, "ignored"u8.ToArray());
        var source = NewSource(handler, gate);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            source.OpenAsync(documentUrl, CancellationToken.None));
        Assert.Equal("documentUrl", ex.ParamName);
        Assert.Empty(gate.Acquired);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task TypedClient_PolitenessConfigured_SendsPoliteUserAgent()
    {
        // The identifying User-Agent is applied on the typed HttpClient, not
        // inside OpenAsync. Resolve that client the way the worker does and
        // capture the header on the wire. A dropped ParseAdd must fail here.
        const string userAgent = "PinballWizard/test (+https://example.test; polite-scraper)";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Politeness:UserAgent"] = userAgent,
            })
            .Build();

        string? sentUserAgent = null;
        var handler = new CallbackHttpHandler((request, _) =>
        {
            sentUserAgent = request.Headers.UserAgent.ToString();
            return Ok("pdf"u8.ToArray());
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPoliteScraping(configuration);
        services.AddRagBackfillService(configuration);
        services.AddHttpClient<HttpDocumentBytesSource>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<HttpDocumentBytesSource>();

        await using var stream = await source.OpenAsync(
            "https://example.com/doc.pdf", CancellationToken.None);

        Assert.True(stream.CanSeek);
        Assert.Equal(userAgent, sentUserAgent);
    }

    [Fact]
    public async Task OpenAsync_EmptyUrl_Throws_WithoutAcquiring()
    {
        var gate = new FakePolitenessGate();
        var source = NewSource(new StatusHttpHandler(HttpStatusCode.OK, "ignored"u8.ToArray()), gate);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.OpenAsync("", CancellationToken.None));
        Assert.Empty(gate.Acquired);
    }

    private static HttpDocumentBytesSource NewSource(HttpMessageHandler handler, FakePolitenessGate gate) =>
        new(
            new HttpClient(handler),
            gate,
            Options.Create(new PolitenessOptions()),
            NullLogger<HttpDocumentBytesSource>.Instance);

    private static HttpResponseMessage Ok(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    private sealed class CallbackHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

        public CallbackHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) =>
            _respond = respond;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request, cancellationToken));
    }

    private sealed class StatusHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _payload;

        public int SendCount { get; private set; }

        public StatusHttpHandler(HttpStatusCode status, byte[] payload)
        {
            _status = status;
            _payload = payload;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            var response = new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_payload),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    // Variant that records the outgoing request URI so tests can assert
    // the http→https upgrade actually rewrote the URL before the fetch.
    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public Uri? LastRequestUri { get; private set; }

        public CapturingHttpHandler(byte[] payload) => _payload = payload;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
            };
            return Task.FromResult(response);
        }
    }
}
