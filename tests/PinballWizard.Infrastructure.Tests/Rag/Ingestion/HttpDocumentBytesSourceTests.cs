using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

// Behavior tests for HttpDocumentBytesSource. Pins the SSRF hardening
// guard (non-http/non-https schemes throw), the http→https legacy-record
// upgrade, and the response-buffering contract (returns a seekable stream
// PdfPig can use).
public sealed class HttpDocumentBytesSourceTests
{
    [Fact]
    public async Task OpenAsync_HttpsUrl_ReturnsSeekableStreamWithBytes()
    {
        var bytes = "fake-pdf-bytes"u8.ToArray();
        var source = NewSource(bytes);

        await using var stream = await source.OpenAsync(
            "https://example.com/doc.pdf", CancellationToken.None);

        Assert.True(stream.CanSeek);
        Assert.Equal(0, stream.Position);
        var buffer = new byte[bytes.Length];
        var read = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(bytes.Length, read);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public async Task OpenAsync_HttpUrl_IsUpgradedToHttpsAndFetchSucceeds()
    {
        // Legacy Cosmos records captured http:// before the scraper enforced
        // https. The source must silently upgrade and fetch — NOT throw.
        // Verify the rewritten https:// URL reaches the HTTP client.
        var bytes = "legacy-pdf-bytes"u8.ToArray();
        var handler = new CapturingHttpHandler(bytes);
        var source = new HttpDocumentBytesSource(
            new HttpClient(handler),
            NullLogger<HttpDocumentBytesSource>.Instance);

        await using var stream = await source.OpenAsync(
            "http://s4.american-pinball.com/img/support/manual.pdf", CancellationToken.None);

        Assert.True(stream.CanSeek);
        Assert.Equal(
            "https://s4.american-pinball.com/img/support/manual.pdf",
            handler.LastRequestUri?.ToString());
    }

    [Theory]
    [InlineData("ftp://example.com/doc.pdf")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    [InlineData("//relative.example/doc.pdf")]
    public async Task OpenAsync_NonHttpAndNonHttpsUrl_ThrowsArgumentException(string documentUrl)
    {
        // SSRF hardening: http:// is upgraded to https:// upstream; all other
        // non-https schemes (ftp://, file://, etc.) indicate source-data
        // corruption or a poisoned change-feed payload and are rejected.
        var source = NewSource("ignored"u8.ToArray());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            source.OpenAsync(documentUrl, CancellationToken.None));
        Assert.Equal("documentUrl", ex.ParamName);
    }

    [Fact]
    public async Task OpenAsync_EmptyUrl_Throws()
    {
        var source = NewSource("ignored"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.OpenAsync("", CancellationToken.None));
    }

    private static HttpDocumentBytesSource NewSource(byte[] payload)
    {
        var handler = new StubHttpHandler(payload);
        var httpClient = new HttpClient(handler);
        return new HttpDocumentBytesSource(
            httpClient,
            NullLogger<HttpDocumentBytesSource>.Instance);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StubHttpHandler(byte[] payload) => _payload = payload;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
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
