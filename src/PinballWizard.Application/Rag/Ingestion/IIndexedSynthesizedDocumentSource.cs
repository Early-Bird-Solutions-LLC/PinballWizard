namespace PinballWizard.Application.Rag.Ingestion;

// One synthesized document present in the RAG search index, projected with enough
// metadata to reconstruct its scraped_documents_raw provenance row. The index stores
// one row per chunk; many chunks share a document_id, so the source de-duplicates to
// one of these per distinct synthesized document.
//
// Every field here is read straight from the index except Title, which is recovered
// from the first chunk's leading "# {title}" header (all four synthesizers write it).
// Title is null when no chunk with that header was found; the backfill then falls
// back to MachineTitle so the document still gets a coherent heading.
public readonly record struct IndexedSynthesizedDocument(
    string DocumentId,
    string MachineId,
    string MachineTitle,
    string Manufacturer,
    string DocumentUrl,
    string DocumentTypeName,
    DateTimeOffset? LastScrapedUtc,
    string? Title);

// Streams the distinct synthesized documents (Kineticist / Tilt Forums / TWIP /
// PB-Freshdesk classes, identified by doc-id prefix) currently in the RAG search
// index. Used by the --backfill-synthesized-raw-docs verb to find synthesized docs
// that are cited in Wizard answers but have no scraped_documents_raw row, so their
// /documents/{id} detail page 404s. Scraped "doc_" documents are excluded — they are
// backed by scraped_documents and out of this backfill's scope.
//
// The default Infrastructure implementation enumerates the AI Search index once and
// de-duplicates in memory (an admin/maintenance read, not a hot path).
public interface IIndexedSynthesizedDocumentSource
{
    IAsyncEnumerable<IndexedSynthesizedDocument> StreamSynthesizedDocumentsAsync(
        CancellationToken cancellationToken);
}
