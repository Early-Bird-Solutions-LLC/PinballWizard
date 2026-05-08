using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Extraction;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace PinballWizard.Infrastructure.Rag.Extraction;

// Phase 4 W1-5 PDF text extractor backed by UglyToad.PdfPig (ADR-0019).
// Returns per-page text + outline for the hybrid chunker (W2-2) to
// consume; surfaces edge cases (encrypted / scanned / malformed) as
// `ExtractionStatus` values rather than exceptions so the Cosmos Change
// Feed Function (W3-2) can log + skip without try/catch.
//
// PdfPig is a pure-sync API; the public ExtractAsync wraps the parse in
// `Task.Run` so a CancellationToken can interrupt waiting workers (the
// in-flight parse itself is not interruptible — PdfPig has no
// cancellation surface — but for typical manual PDFs parse is sub-second).
public sealed class PdfPigDocumentTextExtractor : IDocumentTextExtractor
{
    // Heuristic floor for "this document yielded no extractable text" —
    // treated as scanned-image-only and routed to ExtractionStatus.OcrRequired.
    // 32 chars covers an empty document with metadata-only headers/footers
    // PdfPig sometimes emits; well under what any real manual page produces.
    // Hardcoded for Phase 4's curated subset (~10 PDFs, all known-good
    // modern manuals); revisit-as-PdfExtractionOptions when the Phase 4.5
    // corpus expansion exposes edge cases (e.g., one-line bulletins that
    // are legitimate but short).
    private const int OcrRequiredCharFloor = 32;

    private readonly ILogger<PdfPigDocumentTextExtractor> _logger;

    public PdfPigDocumentTextExtractor(ILogger<PdfPigDocumentTextExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);
        return Task.Run(() => Extract(pdfStream), cancellationToken);
    }

    private ExtractedDocument Extract(Stream pdfStream)
    {
        // The single try/catch wraps both PdfDocument.Open AND every
        // operation that touches the document (GetPages enumeration,
        // page.Text access, TryGetBookmarks). PdfPig is known to throw
        // mid-stream on malformed-but-openable PDFs (truncated content
        // streams, invalid font references, broken xref tables that only
        // surface at page-content time) — moving `using (document)`
        // outside this try would bypass the IDocumentTextExtractor
        // structured-result-on-failure contract and surface a raw PdfPig
        // exception to the Cosmos Change Feed Function (W3-2). Logging
        // is at LogWarning rather than LogError because encrypted /
        // malformed PDFs are an expected outcome during ingestion of a
        // noisy real-world corpus, not an operational failure (the
        // Foundry / AI Search smoke probes log at LogError because their
        // failure surface IS operational — distinct posture).
        try
        {
            using var document = PdfDocument.Open(pdfStream);

            var pages = new List<ExtractedPage>(capacity: document.NumberOfPages);
            var allText = new StringBuilder(capacity: 4096);

            foreach (var page in document.GetPages())
            {
                var pageText = page.Text ?? string.Empty;
                pages.Add(new ExtractedPage(page.Number, pageText));
                if (pageText.Length > 0)
                {
                    allText.AppendLine(pageText);
                }
            }

            // Heuristic: scanned-image-only PDFs parse fine but yield
            // (near-)empty text. Route to OcrRequired so the orchestrator
            // skips chunking + indexing; Phase 4.5 owns the OCR-fallback
            // decision per Phase 4 § Deferred features index.
            if (allText.Length < OcrRequiredCharFloor)
            {
                _logger.LogInformation(
                    "PDF parsed but yielded {Length} chars across {PageCount} pages; classifying as OcrRequired.",
                    allText.Length, document.NumberOfPages);
                return ExtractedDocument.Failure(
                    ExtractionStatus.OcrRequired,
                    $"Document parsed but yielded only {allText.Length} chars across {document.NumberOfPages} pages — likely scanned-image-only. OCR fallback is Phase 4.5.");
            }

            var outline = ExtractOutline(document);

            _logger.LogDebug(
                "PDF extraction succeeded: {PageCount} pages, {OutlineCount} outline entries, {CharCount} chars total.",
                pages.Count, outline.Count, allText.Length);

            return new ExtractedDocument(
                Status: ExtractionStatus.Success,
                Text: allText.ToString(),
                Pages: pages,
                Outline: outline,
                Error: null);
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException ex)
        {
            _logger.LogWarning(ex, "PDF is encrypted; skipping (caller owns the OCR-vs-defer decision per Phase 4.5).");
            return ExtractedDocument.Failure(
                ExtractionStatus.Encrypted,
                $"PDF is encrypted: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PdfPig failed to parse the document; classifying as Malformed.");
            return ExtractedDocument.Failure(
                ExtractionStatus.Malformed,
                $"PdfPig parse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // PdfPig exposes the outline as a tree of `Bookmark` nodes via
    // `document.TryGetBookmarks(...)`. Flatten depth-first into a list
    // with Level tracking so the W2-2 chunker can pick its preferred
    // section-delimiter granularity (top-level only vs. all-levels)
    // per ADR-0019's hybrid-chunking design.
    private static IReadOnlyList<OutlineEntry> ExtractOutline(PdfDocument document)
    {
        if (!document.TryGetBookmarks(out var bookmarks) || bookmarks is null)
        {
            return Array.Empty<OutlineEntry>();
        }

        var entries = new List<OutlineEntry>();
        foreach (var node in bookmarks.Roots)
        {
            FlattenBookmarks(node, level: 0, entries);
        }
        return entries;
    }

    private static void FlattenBookmarks(BookmarkNode node, int level, List<OutlineEntry> entries)
    {
        if (node is DocumentBookmarkNode docNode)
        {
            entries.Add(new OutlineEntry(docNode.Title ?? string.Empty, docNode.PageNumber, level));
        }

        foreach (var child in node.Children)
        {
            FlattenBookmarks(child, level + 1, entries);
        }
    }
}
