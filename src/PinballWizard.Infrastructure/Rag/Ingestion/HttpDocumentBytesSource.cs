using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Default `IDocumentBytesSource` impl. Fetches PDF bytes via HTTP
// GET against the document's source URL captured by the Phase 1
// scraper.
//
// Buffers the full response into a `MemoryStream` because the
// downstream PdfPig extractor requires random access. The
// curated-subset's largest manual is ~80 MB (Stern Godzilla
// service manual), well below the OOM threshold for an ACA
// container with the default 1 GiB memory limit; if Phase 4.5
// brings substantially larger PDFs into scope the fetch should be
// migrated to a temp-file-backed stream.
//
// DELIBERATE EXCEPTION TO POLITE-BY-CONSTRUCTION: this client does
// NOT route through `IPolitenessGate`. The polite-by-construction
// invariant (CLAUDE.md § Locked invariants) targets the discovery /
// crawl path — recurring traffic to source sites' link inventories.
// Document re-downloads here are naturally rare: the pipeline's
// `Skipped_HashUnchanged` short-circuit means a given URL only gets
// re-fetched when the source body actually changes (typically once
// at first ingest, never again until the manufacturer republishes).
// At Phase 4 curated-subset scale (~7 machines × ~5 docs each) total
// outbound is ~35 GETs over the worker's lifetime. The exception is
// architectural, not an oversight; if Phase 4.5 corpus expansion
// brings re-fetch frequency up, wrap the registered HttpClient in
// the politeness gate at the `AddHttpClient<IDocumentBytesSource,
// HttpDocumentBytesSource>` registration site.
//
// SSRF hardening: documentUrl flows in from the Cosmos
// `scraped_documents` change feed, which only the scraper's MI can
// write — but defense-in-depth costs nothing here. We require
// `https://` scheme so a future scraper bug or compromised
// dependency can't drop a `http://169.254.169.254/...` URL into
// the source container and trick the worker into hitting the ACA
// instance metadata endpoint. (The metadata endpoint also requires
// a `Metadata: true` header the standard HttpClient doesn't send,
// so this guard is a redundant second layer.)
public sealed class HttpDocumentBytesSource : IDocumentBytesSource
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpDocumentBytesSource> _logger;

    public HttpDocumentBytesSource(
        HttpClient httpClient,
        ILogger<HttpDocumentBytesSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<Stream> OpenAsync(
        string documentUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUrl);

        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"documentUrl must be an absolute https:// URL; got '{documentUrl}'. Phase 1 scrapers only emit https sources; non-https here indicates source-data corruption or a poisoned change-feed payload.",
                nameof(documentUrl));
        }

        _logger.LogDebug("RAG document fetch: GET {Url}", documentUrl);

        using var response = await _httpClient
            .GetAsync(documentUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var buffer = new MemoryStream();
        await using (var contentStream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await contentStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return buffer;
    }
}
