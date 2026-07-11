namespace PinballWizard.Application.Rag.Coverage;

// Read-side queries the coverage prober needs against the RAG index. The
// implementation (Infrastructure) translates a RagSource recognizer into an
// OData filter; the port keeps Application infra-free.
public interface ICorpusIndexQuery
{
    // Total indexed chunks matching the source's recognizer.
    Task<long> CountAsync(RagSource source, CancellationToken ct);

    // Distinct document_type values (with chunk counts) that have content for
    // this source — the live (source × doc-type) cells.
    Task<IReadOnlyList<DocTypeCount>> FacetDocumentTypesAsync(RagSource source, CancellationToken ct);

    // One sample chunk for a (source, document_type) cell, or null if none.
    Task<CorpusSample?> SampleAsync(RagSource source, string documentType, CancellationToken ct);
}

public sealed record DocTypeCount(string DocumentType, long ChunkCount);

public sealed record CorpusSample(
    string DocumentId,
    string Manufacturer,
    string DocumentType,
    string MachineTitle,
    string SectionHeading);
