namespace PinballWizard.Application.Documents;

// Reclassifies stored raw documents in-place by re-running the same
// ClassifyDocumentType logic over each record's existing Source fields
// (LinkText, FileUrl, DiscoveryContext) and writing back the updated
// document_type ONLY when it changed.
//
// No external HTTP calls are made — the operation works entirely on
// already-stored Cosmos data (polite-by-construction requirement).
//
// The write-back updates both scraped_documents_raw (document_type field)
// and (via the linker fan-out already recorded in scraped_documents) the
// change-feed source container the RAG ingestion worker watches. The
// operator must run --relink-all after --reclassify-documents to fan the
// updated type into scraped_documents so the change feed re-emits it.
// Alternatively, --run-rag-backfill performs a full re-ingest from the
// raw change-feed stream iterator and picks up the updated types directly.
public interface IDocumentReclassifier
{
    Task<ReclassifyResult> RunAsync(CancellationToken cancellationToken);
}

public readonly record struct ReclassifyResult(
    int Scanned,
    int Reclassified,
    int Unchanged,
    int Failed,
    IReadOnlyList<ReclassifyTransition> Transitions);

// Records a single document whose classification changed.
public readonly record struct ReclassifyTransition(
    string DocumentId,
    string DocumentUrl,
    string OldType,
    string NewType);
