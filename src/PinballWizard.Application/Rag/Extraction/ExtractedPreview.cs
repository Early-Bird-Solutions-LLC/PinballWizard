namespace PinballWizard.Application.Rag.Extraction;

// The result of IDocumentPreviewExtractor.ExtractPreviewAsync (#832).
//
// Deliberately NOT an ExtractedDocument: a preview carries only the first N
// pages — no whole-document Text, no Outline — so a truncated parse is
// type-incompatible with the chunking/indexing path and can never be indexed
// as if complete. The bad state is unrepresentable, not merely discouraged.
public sealed record ExtractedPreview(
    ExtractionStatus Status,
    IReadOnlyList<ExtractedPage> Pages,
    string? Error)
{
    public static ExtractedPreview Failure(ExtractionStatus status, string error) => new(
        Status: status,
        Pages: [],
        Error: error);
}
