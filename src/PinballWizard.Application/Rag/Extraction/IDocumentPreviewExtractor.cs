namespace PinballWizard.Application.Rag.Extraction;

// Page-limited PDF text extraction for callers that consume only the first
// few pages (#832 — DocumentLinker reads pages 1-2 for its page tiers).
//
// This is a deliberately narrow sibling of IDocumentTextExtractor, NOT a
// method on it: the ADI extractor cannot honor a memory bound (its
// ReadToBytesAsync materializes the whole blob before the page-range
// parameter limits anything), and FallbackDocumentTextExtractor delegates to
// ADI only on OcrRequired — a status the preview path never produces. A
// required method there would be dead code satisfying a contract it cannot
// honor. Only PdfPigDocumentTextExtractor implements this.
//
// Producible Status values are exactly: Success, Encrypted, Malformed,
// SizeExceeded. OcrRequired (heuristic deliberately skipped — an empty
// preview simply yields no linking evidence) and OcrFailed (no ADI in this
// path) never appear.
public interface IDocumentPreviewExtractor
{
    // Extract text from the first `pageCount` pages (1-based, capped at the
    // document's page count). The stream is read but not disposed; it MUST
    // be seekable (PDF cross-reference parsing needs random access).
    Task<ExtractedPreview> ExtractPreviewAsync(
        Stream pdfStream,
        int pageCount,
        CancellationToken cancellationToken);
}
