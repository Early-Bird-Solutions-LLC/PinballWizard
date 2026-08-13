using Microsoft.Extensions.Logging;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// IDocumentBytesSource decorator that serves document bytes from the pinwiz-raw
// blob container when the blob key is known, falling back to the inner (HTTP)
// source on a blob miss or when the blob key cannot be derived from the input.
//
// BLOB KEY DERIVATION CONSTRAINT:
// Blob names follow the convention {sourceType}/{filename} (e.g.
// "stern_manuals/Godzilla_Pro_web.pdf"). The RAG change-feed handler currently
// calls OpenAsync with the raw document URL from RagSourceDocument.DocumentUrl
// (e.g. "https://sternpinball.com/.../Godzilla_Pro_web.pdf"). Because the
// scraped_documents Cosmos record does NOT carry source_type, the full blob key
// cannot be derived from the URL alone.
//
// As a result, this implementation distinguishes two input shapes:
//
//   1. Blob name  — input contains a '/' and does NOT start with a URL scheme
//      (https:// / http://). This is the forward path that Task 3/4 will produce
//      once the stored blob path is threaded through RagSourceDocument and the
//      IDocumentBytesSource caller. The blob store is probed; a miss falls through
//      to HTTP.
//
//   2. URL input  — input starts with https:// or http:// or is not a valid
//      blob-name shape. The HTTP fallback is invoked directly. A non-https URL
//      falls through to the inner source which enforces the https-only SSRF guard.
//
// INVARIANT #17 (no masking fallbacks):
//   - A blob MISS (not yet uploaded) → HTTP fallback. This is a genuine fetch, not
//     masking — the document is freshly scraped and the downloader (Task 3) has not
//     yet written it. Logged at Debug.
//   - A blob ERROR (auth failure, storage outage, etc.) → propagated. The hosted
//     service's dead-letter path handles it. Never swallowed silently.
//
// FOLLOW-UP (Task 3/4): thread `blob_path` through ScrapedDocumentRecord,
// RagSourceDocument, and the ScrapedDocumentChangeFeedHandler caller so this
// source receives the exact blob key and can serve from blob for all documents
// that have been downloaded. Until then, the blob lookup path is exercised only
// when the caller explicitly passes a blob-name string (e.g. from a future
// blob-path-aware backfill service).
public sealed class BlobDocumentBytesSource : IDocumentBytesSource
{
    private readonly IDocumentBlobStore _store;
    private readonly Func<IDocumentBytesSource> _httpFallbackFactory;
    private readonly ILogger<BlobDocumentBytesSource> _logger;

    // Takes a FACTORY for the HTTP fallback (not a captured instance) so a fresh
    // typed-client-backed HttpDocumentBytesSource is resolved per HTTP fallback call —
    // preserving IHttpClientFactory handler rotation rather than pinning one client
    // for the life of this singleton decorator.
    public BlobDocumentBytesSource(
        IDocumentBlobStore store,
        Func<IDocumentBytesSource> httpFallbackFactory,
        ILogger<BlobDocumentBytesSource> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(httpFallbackFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _httpFallbackFactory = httpFallbackFactory;
        _logger = logger;
    }

    public async Task<Stream> OpenAsync(string documentUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUrl);

        // Determine whether the input is a blob name or a URL.
        // A blob name contains '/' (e.g. "stern_manuals/file.pdf") and does NOT begin
        // with a URL scheme. An https:// or http:// input is a source URL — the blob
        // store cannot be probed without knowing the sourceType prefix, so we delegate
        // to HTTP immediately. Non-https URLs also fall through to the HTTP source so
        // the inner source's SSRF guard fires.
        if (LooksLikeBlobName(documentUrl))
        {
            return await TryServeBlobAsync(documentUrl, cancellationToken).ConfigureAwait(false);
        }

        // Input is a URL (or malformed). Delegate to HTTP fallback.
        _logger.LogDebug(
            "BlobDocumentBytesSource: input is a URL, not a blob key — delegating to HTTP source. Input: {Input}",
            documentUrl);
        return await _httpFallbackFactory().OpenAsync(documentUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream> TryServeBlobAsync(string blobName, CancellationToken cancellationToken)
    {
        // Probe the blob store. ExistsAsync false = miss → HTTP fallback (genuine fetch,
        // not masking). Any exception from ExistsAsync propagates (invariant #17: blob
        // errors must surface, not be swallowed).
        var exists = await _store.ExistsAsync(blobName, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            _logger.LogDebug(
                "BlobDocumentBytesSource: blob '{BlobName}' not found in pinwiz-raw — delegating to HTTP fallback.",
                blobName);
            return await _httpFallbackFactory().OpenAsync(blobName, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("BlobDocumentBytesSource: blob hit for '{BlobName}'.", blobName);

        // OpenReadAsync returns a seekable temp-file-backed stream positioned
        // at 0 (#832 — no longer a MemoryStream), satisfying PdfPig's
        // random-access requirement without heap cost proportional to size.
        return await _store.OpenReadAsync(blobName, cancellationToken).ConfigureAwait(false);
    }

    // Returns true when the input looks like a blob name of the form
    // "{sourceType}/{filename}" — i.e., contains '/' but does not start with a URL
    // scheme (https://, http://, ftp://, etc.). This is a heuristic: the blob name
    // convention is {sourceType}/{filename} (forward slash, no scheme prefix).
    private static bool LooksLikeBlobName(string input)
    {
        // Contains a slash but is not a URL (no "://" sequence).
        return input.Contains('/', StringComparison.Ordinal)
            && !input.Contains("://", StringComparison.Ordinal);
    }
}
