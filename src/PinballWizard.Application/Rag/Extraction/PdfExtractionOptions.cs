using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Application.Rag.Extraction;

// Configuration for the Phase 4 PDF text extractor (W1-5
// PdfPigDocumentTextExtractor). Defaults are sensible for the curated
// 7-machine subset; tuned at H3 calibration if Phase 4.5 corpus
// expansion surfaces edge cases.
//
// Hoisted from inline constants per the W1-5 local-review's deferred
// ⚠️ findings:
//   - `OcrRequiredCharFloor` was a magic 32 in the extractor; tunable
//     here for environments with thinner / shorter legitimate PDFs.
//   - `MaxStreamBytes` is new in this PR — protects against zip-bomb
//     / multi-GB hostile uploads. Phase 4 curated subset PDFs are
//     bounded (largest is ~30MB); 100MB default leaves headroom for
//     legitimate Phase 4.5 manuals while capping unbounded reads.
public sealed class PdfExtractionOptions
{
    public const string SectionName = "Rag:PdfExtraction";

    // Maximum size in bytes the extractor will accept. A stream
    // larger than this returns ExtractionStatus.SizeExceeded without
    // attempting to parse — PdfPig opens the stream lazily but
    // GetPages() materializes content streams, and a multi-GB PDF
    // would balloon process memory before the parser realizes
    // anything is wrong. The check fires when the stream is seekable;
    // non-seekable streams skip the check (PdfPig requires seekable
    // input for cross-reference parsing, so a non-seekable stream
    // fails downstream regardless).
    [Range(typeof(long), "1024", "9223372036854775807")]
    public long MaxStreamBytes { get; set; } = 100L * 1024 * 1024;

    // Threshold below which a successfully-parsed PDF is classified as
    // OcrRequired (scanned-image-only). 32 chars covers an empty
    // document with metadata-only headers/footers PdfPig sometimes
    // emits; well under what any real manual page produces. Phase 4.5
    // corpus expansion may surface legitimate one-line bulletins
    // shorter than this — tunable here when that happens.
    [Range(0, 4096)]
    public int OcrRequiredCharFloor { get; set; } = 32;
}
