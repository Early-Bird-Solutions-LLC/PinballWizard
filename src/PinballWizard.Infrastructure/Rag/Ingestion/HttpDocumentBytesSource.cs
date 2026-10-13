using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

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
// POLITE-BY-CONSTRUCTION: this type extends `PoliteScraperBase` and
// sends through `SendPolitelyAsync` (robots.txt, per-origin throttle,
// 429 reporting). It is not an `ISourceScraper`; the base is the
// shared choke point for any outbound manufacturer HTTP call,
// including the ingestion fallback that used to call
// `HttpClient.GetAsync` directly (#760). `ResponseHeadersRead` keeps
// a large PDF from being buffered by HttpClient before we copy it.
// A non-success status — including 403 from a manufacturer that
// refuses Azure egress — throws. Callers dead-letter that exception.
// This method never substitutes an empty or placeholder stream.
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
// `http://` URLs are silently upgraded to `https://` before this
// check to accommodate legacy Cosmos records captured before the
// scraper enforced https; the guard therefore rejects all non-http
// and non-https schemes (ftp://, file://, etc.). Rejected URLs
// never reach the gate or the wire.
public sealed class HttpDocumentBytesSource : PoliteScraperBase, IDocumentBytesSource
{
    private readonly HttpClient _httpClient;

    public HttpDocumentBytesSource(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<HttpDocumentBytesSource> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<Stream> OpenAsync(
        string documentUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUrl);

        // Some legacy Cosmos records captured http:// URLs before the scraper enforced
        // https. Silently upgrade http→https: manufacturer CDNs serve over TLS and the
        // SSRF guard below still fires for every other non-https scheme (ftp://, file://,
        // gopher://, etc.), so the security invariant is preserved.
        if (documentUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("RAG document fetch: upgrading http:// to https:// for '{Url}' — legacy Cosmos record.", documentUrl);
            documentUrl = string.Concat("https://", documentUrl.AsSpan(7));
        }

        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"documentUrl must be an absolute https:// URL after http→https upgrade; got '{documentUrl}'. " +
                "A non-http/non-https scheme (ftp://, file://, etc.) here indicates source-data corruption or a poisoned change-feed payload " +
                "(http:// is silently upgraded before this check).",
                nameof(documentUrl));
        }

        Logger.LogDebug("RAG document fetch: GET {Url}", parsed);

        using var request = new HttpRequestMessage(HttpMethod.Get, parsed);
        using var response = await SendPolitelyAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        // 403 Forbidden (Azure-egress blocks), other 4xx, and 5xx are failures.
        // EnsureSuccessStatusCode throws HttpRequestException with the status code
        // so the change-feed dead-letter records the real error. Do not map a
        // failure onto an empty stream. SendPolitelyAsync has already reported
        // the status to the gate, including a 429-streak abort.
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
