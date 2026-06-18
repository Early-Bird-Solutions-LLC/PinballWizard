using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

// Decorator that adds an ADI OCR fallback to PdfPig extraction.
// When PdfPig returns OcrRequired (near-zero text), delegates to the
// ADI extractor. All other PdfPig statuses (Success, Encrypted,
// Malformed, SizeExceeded) are returned as-is. Callers see only
// Success or a terminal failure status — the fallback chain is
// invisible to IChunker and IRagIndexer.
//
// The fallback parameter is typed as IDocumentTextExtractor so tests
// can inject a stub without requiring a live ADI endpoint. In
// production DI, AzureDocumentIntelligenceExtractor is the concrete
// implementation bound to this slot.
//
// Registered as the IDocumentTextExtractor singleton when
// DocumentIntelligence:Endpoint is present (Phase 4.5 W1). When the
// key is absent, the DI container keeps PdfPigDocumentTextExtractor
// as the sole implementation (Phase 4 behaviour unchanged).
public sealed class FallbackDocumentTextExtractor : IDocumentTextExtractor
{
    private readonly PdfPigDocumentTextExtractor _primary;
    private readonly IDocumentTextExtractor _fallback;
    private readonly ILogger<FallbackDocumentTextExtractor> _logger;

    public FallbackDocumentTextExtractor(
        PdfPigDocumentTextExtractor primary,
        AzureDocumentIntelligenceExtractor fallback,
        ILogger<FallbackDocumentTextExtractor> logger)
        : this(primary, (IDocumentTextExtractor)fallback, logger) { }

    internal FallbackDocumentTextExtractor(
        PdfPigDocumentTextExtractor primary,
        IDocumentTextExtractor fallback,
        ILogger<FallbackDocumentTextExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(logger);

        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        var primary = await _primary.ExtractAsync(pdfStream, cancellationToken);

        if (primary.Status != ExtractionStatus.OcrRequired)
            return primary;

        _logger.LogInformation(
            "PdfPig returned OcrRequired — delegating to ADI Read model fallback");

        // Reset stream position for the ADI call; PdfPig may have consumed it.
        if (pdfStream.CanSeek)
            pdfStream.Seek(0, SeekOrigin.Begin);

        return await _fallback.ExtractAsync(pdfStream, cancellationToken);
    }
}
