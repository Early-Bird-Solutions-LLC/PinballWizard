using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

// ADI Read-model extractor. Invoked only by FallbackDocumentTextExtractor
// when PdfPig returns OcrRequired (near-zero text — likely scanned image).
// Returns Success + extracted text on ADI success, or OcrFailed when ADI
// also returns empty content. Never returns OcrRequired.
public sealed class AzureDocumentIntelligenceExtractor : IDocumentTextExtractor
{
    private readonly DocumentIntelligenceClient _client;
    private readonly ILogger<AzureDocumentIntelligenceExtractor> _logger;

    public AzureDocumentIntelligenceExtractor(
        IOptions<DocumentIntelligenceOptions> options,
        ILogger<AzureDocumentIntelligenceExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var endpoint = new Uri(options.Value.Endpoint);
        _client = new DocumentIntelligenceClient(endpoint, new DefaultAzureCredential());
        _logger = logger;
    }

    // Test seam: accepts a pre-built client so tests can mock ADI without a
    // live endpoint. Mirrors the pattern in FallbackDocumentTextExtractor.
    internal AzureDocumentIntelligenceExtractor(
        DocumentIntelligenceClient client,
        ILogger<AzureDocumentIntelligenceExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    public async Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        _logger.LogInformation("ADI Read model: starting OCR extraction");

        try
        {
            var bytes = await ReadToBytesAsync(pdfStream, cancellationToken);
            var analyzeOptions = new AnalyzeDocumentOptions("prebuilt-read", bytes);

            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                analyzeOptions,
                cancellationToken);

            var result = operation.Value;
            var text = result.Content ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("ADI Read model returned empty content — document unrecoverable");
                return ExtractedDocument.Failure(ExtractionStatus.OcrFailed,
                    "ADI Read model returned empty content after OCR attempt.");
            }

            var pages = result.Pages
                .Select((p, i) => new ExtractedPage(i + 1, string.Join(" ", p.Words.Select(w => w.Content))))
                .ToList();

            _logger.LogInformation(
                "ADI Read model: extracted {CharCount} chars across {PageCount} pages",
                text.Length, pages.Count);

            return new ExtractedDocument(
                Status: ExtractionStatus.Success,
                Text: text,
                Pages: pages,
                Outline: [],
                Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ADI Read model threw an exception during extraction");
            return ExtractedDocument.Failure(ExtractionStatus.OcrFailed,
                $"ADI extraction failed: {ex.Message}");
        }
    }

    private static async Task<BinaryData> ReadToBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return BinaryData.FromBytes(ms.ToArray());
    }
}
