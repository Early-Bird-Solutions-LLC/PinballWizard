using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Rag.Ingestion;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

/// <summary>
/// Tests for <see cref="BlobDocumentBytesSource"/> — a decorator that attempts to
/// serve document bytes from the pinwiz-raw blob container, falling back to the
/// inner (HTTP) source on a blob miss or when the blob key cannot be derived from
/// the document URL alone (e.g. source_type is unavailable from the change-feed record).
/// </summary>
public sealed class BlobDocumentBytesSourceTests
{
    // Blob names follow {sourceType}/{filename}. Because the RAG change-feed record
    // (RagSourceDocument) does not carry source_type, BlobDocumentBytesSource cannot
    // derive the full blob key from the URL alone and therefore always delegates to
    // the HTTP fallback. This test suite documents that behavior so it is explicit
    // and reviewable; the fix (add blob_path to RagSourceDocument / thread it through
    // IDocumentBytesSource) is tracked as a follow-up (Task 3/4 scope).

    private readonly IDocumentBlobStore _store = Substitute.For<IDocumentBlobStore>();
    private readonly RecordingFallback _fallback = new();
    private readonly BlobDocumentBytesSource _sut;

    public BlobDocumentBytesSourceTests()
    {
        _sut = new BlobDocumentBytesSource(
            _store,
            () => _fallback,
            NullLogger<BlobDocumentBytesSource>.Instance);
    }

    [Fact]
    public async Task OpenAsync_BlobNameProvided_BlobExists_ReturnsBlobStream_FallbackNotCalled()
    {
        // When the caller supplies the exact blob name (Task 3/4 will thread this through),
        // OpenAsync serves bytes from the blob store without touching the HTTP fallback.
        var blobName = "stern_manuals/Godzilla_Pro_web.pdf";
        var expected = Encoding.UTF8.GetBytes("pdf bytes from blob");
        _store.ExistsAsync(blobName, Arg.Any<CancellationToken>()).Returns(true);
        _store.OpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new MemoryStream(expected)));

        await using var result = await _sut.OpenAsync(blobName, CancellationToken.None);

        using var buffer = new MemoryStream();
        await result.CopyToAsync(buffer);
        Assert.Equal(expected, buffer.ToArray());
        Assert.True(result.CanSeek, "returned stream must be seekable for PdfPig random access");
        Assert.Equal(0, _fallback.Calls);
    }

    [Fact]
    public async Task OpenAsync_BlobNameProvided_BlobAbsent_DelegatesToFallback()
    {
        // Blob miss (ExistsAsync returns false) → delegate to HTTP fallback.
        // This is NOT a masking fallback: the blob is genuinely not there yet
        // (e.g. freshly scraped doc before Task 3 writes it), so HTTP is correct.
        var blobName = "stern_manuals/NotYetDownloaded.pdf";
        _store.ExistsAsync(blobName, Arg.Any<CancellationToken>()).Returns(false);

        await using var result = await _sut.OpenAsync(blobName, CancellationToken.None);

        Assert.Equal(1, _fallback.Calls);
        Assert.Equal(blobName, _fallback.LastUrl);
        await _store.DidNotReceive().OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_NonHttpsUrl_DelegatesToFallback_BlobNotConsulted()
    {
        // SSRF parity: when the input looks like an http/non-https URL (not a blob name),
        // BlobDocumentBytesSource does not probe the blob store. Delegates to HTTP
        // fallback (which enforces https and rejects the poisoned payload).
        var url = "http://169.254.169.254/latest/meta-data/Godzilla_Pro_web.pdf";

        await using var result = await _sut.OpenAsync(url, CancellationToken.None);

        Assert.Equal(1, _fallback.Calls);
        await _store.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_BlobStoreError_NotFound_DelegatesToFallback()
    {
        // If ExistsAsync returns false (404-equivalent), treat as miss → HTTP fallback.
        // Mirrors the blob miss case to ensure a storage "not found" signal doesn't surface
        // as an exception visible to the pipeline.
        var blobName = "stern_manuals/SomeDoc.pdf";
        _store.ExistsAsync(blobName, Arg.Any<CancellationToken>()).Returns(false);

        await using var result = await _sut.OpenAsync(blobName, CancellationToken.None);

        Assert.Equal(1, _fallback.Calls);
    }

    [Fact]
    public async Task OpenAsync_BlobStoreUnexpectedError_Propagates_FallbackNotCalled()
    {
        // Invariant #17: a blob *error* (not a miss) must surface, not be swallowed.
        // A storage outage or auth failure must propagate to the hosted service's
        // dead-letter path, not silently fall through to HTTP (which would mask the error).
        var blobName = "stern_manuals/SomeDoc.pdf";
        _store.ExistsAsync(blobName, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("storage auth failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.OpenAsync(blobName, CancellationToken.None));

        Assert.Equal(0, _fallback.Calls);
    }

    [Fact]
    public async Task OpenAsync_HttpsUrlWithNoSourceType_DelegatesToFallback_BlobNotConsulted()
    {
        // Design constraint: when given a bare https:// document URL (as the RAG change-feed
        // handler currently passes — see ScrapedDocumentChangeFeedHandler.cs), BlobDocumentBytesSource
        // cannot derive the full {sourceType}/{filename} blob key. It must delegate to HTTP
        // rather than probe the blob store with an incorrect/guessed key.
        // This test documents the current behavior. The fix is tracked as a Task 3/4 follow-up
        // (thread blob_path through RagSourceDocument and IDocumentBytesSource caller).
        var url = "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf";

        await using var result = await _sut.OpenAsync(url, CancellationToken.None);

        Assert.Equal(1, _fallback.Calls);
        Assert.Equal(url, _fallback.LastUrl);
        // Blob store must NOT be probed — we don't have the sourceType to form a valid blob key.
        await _store.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_UrlInput_AcquiresPolitenessGate_AndSurfaces403()
    {
        // The change-feed path passes a manufacturer URL when no blob name is
        // stored. That used to be a bare HttpClient.GetAsync. The decorator must
        // still delegate, the gate must acquire before the send, and a 403 must
        // propagate instead of coming back as document bytes.
        var url = "https://spookypinball.com/wp-content/uploads/Halloween-and-Ultraman-Manual.pdf";
        var gate = new FakePolitenessGate();
        var acquiredBeforeSend = false;
        var handler = new ForbiddenAfterAcquireHandler(() => acquiredBeforeSend = gate.Acquired.Count == 1);
        var http = new HttpDocumentBytesSource(
            new HttpClient(handler),
            gate,
            Options.Create(new PolitenessOptions()),
            NullLogger<HttpDocumentBytesSource>.Instance);
        var sut = new BlobDocumentBytesSource(
            _store,
            () => http,
            NullLogger<BlobDocumentBytesSource>.Instance);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.OpenAsync(url, CancellationToken.None));

        Assert.True(acquiredBeforeSend);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal(new Uri(url), Assert.Single(gate.Acquired));
        Assert.Equal(HttpStatusCode.Forbidden, Assert.Single(gate.Reported).Status);
        Assert.Equal(1, handler.SendCount);
        await _store.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class ForbiddenAfterAcquireHandler : HttpMessageHandler
    {
        private readonly Action _onSend;

        public int SendCount { get; private set; }

        public ForbiddenAfterAcquireHandler(Action onSend) => _onSend = onSend;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            _onSend();
            // Returned to HttpClient, which disposes it. Do not dispose here —
            // a using would release the message before the caller reads the 403.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new ByteArrayContent("blocked-by-waf"u8.ToArray()),
                RequestMessage = request,
            });
        }
    }

    private sealed class RecordingFallback : IDocumentBytesSource
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<Stream> OpenAsync(string documentUrl, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = documentUrl;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("http fallback bytes")));
        }
    }
}
