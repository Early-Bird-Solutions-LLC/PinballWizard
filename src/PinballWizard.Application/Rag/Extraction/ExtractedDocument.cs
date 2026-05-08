namespace PinballWizard.Application.Rag.Extraction;

// The result of `IDocumentTextExtractor.ExtractAsync`. Aggregates the
// full extracted text, per-page text (for page-anchored citations per
// ADR-0019 § citation surface), the outline (used by ADR-0019's
// hybrid chunker as section delimiter), and an Error string when
// Status indicates failure.
//
// Wave 2 W2-2 (HybridChunker) consumes Pages + Outline; Wave 3 W3-2
// (Cosmos Change Feed Function) consumes Status to decide whether to
// continue or log+skip; both consume Text.
public sealed record ExtractedDocument(
    ExtractionStatus Status,
    string Text,
    IReadOnlyList<ExtractedPage> Pages,
    IReadOnlyList<OutlineEntry> Outline,
    string? Error)
{
    // Conventional empty result for failure paths so callers don't
    // need to construct empty arrays at every call site. Status is
    // expected to be a non-Success value when this is used.
    public static ExtractedDocument Failure(ExtractionStatus status, string error) => new(
        Status: status,
        Text: string.Empty,
        Pages: [],
        Outline: [],
        Error: error);
}

// One page worth of extracted text, indexed by 1-based PageNumber to
// match the user-facing convention (and PdfPig's Page.Number which is
// also 1-based). Text may be empty if the page contains only images.
public sealed record ExtractedPage(int PageNumber, string Text);

// One bookmark / outline entry from the PDF's outline tree. Phase 4
// hybrid chunker (ADR-0019) uses this as the section delimiter:
// chunks never cross a Title boundary. Level is the depth in the
// outline tree (0 = top-level chapter, 1 = section, etc.) — exposed
// because some PDFs nest deeply and the chunker may want to elevate
// only top-level boundaries when a document is heading-rich.
public sealed record OutlineEntry(string Title, int PageNumber, int Level);
