namespace PinballWizard.Application.Rag.Extraction;

// Phase 4 RAG document text extraction abstraction (ADR-0019).
// Implementations parse a PDF stream, return per-page text + outline
// for downstream chunking, and surface edge cases (encrypted /
// scanned-image-only / malformed) as a status enum rather than an
// exception so callers can log and skip without try/catch.
//
// Two implementations ship: `Infrastructure.Rag.Extraction.PdfPigDocumentTextExtractor`
// (primary, wrapping `UglyToad.PdfPig` 0.1.x) and
// `Infrastructure.Rag.Extraction.AzureDocumentIntelligenceExtractor`
// (ADI Read-model OCR fallback, Phase 4.5 W1). Production DI wires
// both through `FallbackDocumentTextExtractor` when
// `DocumentIntelligence:Endpoint` is configured.
public interface IDocumentTextExtractor
{
    // Extract text + structure from the PDF in `pdfStream`. The stream
    // is read but not disposed by the implementation — caller owns the
    // lifetime. Stream MUST be seekable (PDF cross-reference parsing
    // needs random access). Cancellation is honored at parse boundaries
    // (PdfPig itself is sync; the implementation wraps in Task.Run so
    // CancellationToken can interrupt waiting workers but cannot
    // interrupt an in-flight parser).
    Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken);
}
