namespace PinballWizard.Application.Rag.Ingestion;

// Abstraction over "fetch the bytes for a document URL" so the
// W3-2 hosted service can read PDF content without coupling to a
// specific storage substrate.
//
// Today the default Infrastructure implementation is HTTP-backed
// (`HttpDocumentBytesSource`) — it fetches the PDF directly from
// the original source URL captured by the Phase 1 scraper. A
// future blob-storage migration (Phase 4.5 work) ships a
// `BlobDocumentBytesSource` impl reading from `pinwiz-raw`; the
// hosted service consumes either via the same interface.
//
// Returns an open, seekable Stream the caller is responsible for
// disposing. The implementation buffers into a `MemoryStream` so
// PdfPig (which requires random access) works regardless of the
// underlying transport.
public interface IDocumentBytesSource
{
    Task<Stream> OpenAsync(
        string documentUrl,
        CancellationToken cancellationToken);
}
