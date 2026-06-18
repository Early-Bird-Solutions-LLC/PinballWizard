namespace PinballWizard.Application.Rag.Extraction;

// Outcome enum for IDocumentTextExtractor.ExtractAsync. Per ADR-0019 §
// no-outline fallback and the Phase 4 § Non-goals "OCR fallback for
// scanned / encrypted PDFs" item, the extractor identifies edge cases
// up front so the orchestrator (Wave 2 W2-2 chunker, Wave 3 W3-2 Cosmos
// Change Feed Function) can branch deliberately rather than silently
// dropping content.
//
// Phase 4: Success → continue to chunking; OcrRequired / Encrypted /
// Malformed → log and skip (treated as a known coverage gap).
// Phase 4.5 makes the OCR-vs-defer decision (Azure Document Intelligence
// vs. accepting the gap) per Phase 4 § Deferred features index.
public enum ExtractionStatus
{
    // Text was extracted successfully and the document is well-formed.
    Success,

    // The document parsed structurally but yielded no extractable text
    // (or so little that it's almost certainly a scanned image-only
    // PDF). OCR — image → text recognition — is the only way to
    // recover content; Phase 4 logs and skips, Phase 4.5 owns the
    // OCR-fallback decision.
    OcrRequired,

    // The document is password-protected and the extractor was not
    // given a password. PdfPig refuses to open without one. Phase 4
    // logs and skips; if a curated-subset machine's manual hits this,
    // swap for the documented alternate per Phase 4 § Scope item 7's
    // slot. P4-R4 risk-register entry.
    Encrypted,

    // The PDF parser raised an exception (truncated file, invalid
    // cross-reference table, unsupported feature, etc.). Distinct from
    // Encrypted (a structural refusal-to-open) and OcrRequired (a
    // valid-but-imageless document). Logs the parser error message in
    // ExtractedDocument.Error.
    Malformed,

    // The input stream's length exceeds PdfExtractionOptions.MaxStreamBytes.
    // Distinct from Malformed because the cause is operational
    // (oversized upload — possibly a zip-bomb or hostile multi-GB
    // PDF) rather than a parser-level structural issue. The
    // orchestrator (W3-2 Cosmos Change Feed Function) treats this
    // identically to Malformed (log + skip), but the distinct status
    // gives operational telemetry a way to count size-rejections
    // separately from parse failures.
    SizeExceeded,

    // PdfPig returned 0 (or near-zero) tokens AND the Azure Document
    // Intelligence OCR fallback also returned 0 tokens. The document
    // is permanently unrecoverable with available tooling; the
    // orchestrator logs it and skips. Distinct from OcrRequired
    // (where ADI hasn't been attempted yet) so telemetry can
    // distinguish "ADI not configured" from "ADI tried and failed".
    OcrFailed,
}
