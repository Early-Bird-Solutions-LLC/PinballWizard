using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Extraction;

// Unit tests for PdfPigDocumentTextExtractor. Fixture PDFs are generated
// programmatically via PdfPig's own writer (UglyToad.PdfPig.Writer)
// rather than committed as binary blobs — keeps the test suite
// self-contained and deterministic. Per the build-spec § Phase 4 scope
// item 14, fixture coverage spans success, scanned-image-only
// (OcrRequired), malformed input, and basic outline extraction.
public sealed class PdfPigDocumentTextExtractorTests
{
    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfPigDocumentTextExtractor(null!));
    }

    [Fact]
    public async Task ExtractAsync_NullStream_Throws()
    {
        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => extractor.ExtractAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_MalformedBytes_ReturnsMalformedStatus()
    {
        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);
        // Random bytes that do NOT begin with the %PDF- magic header.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf, just plain text"));

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
        Assert.Empty(result.Text);
        Assert.Empty(result.Pages);
        Assert.Empty(result.Outline);
        Assert.NotNull(result.Error);
        Assert.Contains("PdfPig parse failed", result.Error);
    }

    [Fact]
    public async Task ExtractAsync_EmptyStream_ReturnsMalformedStatus()
    {
        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);
        using var stream = new MemoryStream();

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExtractAsync_PdfWithText_ReturnsSuccessWithExtractedText()
    {
        var pdfBytes = BuildPdfWithText("Hello pinball world. " +
            "This is a synthetic fixture document with enough text to clear the OCR-required floor heuristic. " +
            "It has one page and no outline entries — the chunker would treat it as no-outline-fallback.");

        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Contains("Hello pinball world", result.Text);
        Assert.Contains("synthetic fixture document", result.Text);
        Assert.Single(result.Pages);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Contains("Hello pinball world", result.Pages[0].Text);
        // No outline entries because BuildPdfWithText doesn't add bookmarks.
        Assert.Empty(result.Outline);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ExtractAsync_PdfWithMultiplePages_PreservesPageNumbers()
    {
        var pdfBytes = BuildPdfWithPages(
            "Page one — hello pinball world, this is the first page with enough text to clear the floor.",
            "Page two — second page text, also with enough characters to pass the heuristic.",
            "Page three — third and final page of the synthetic fixture document.");

        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Equal(3, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(2, result.Pages[1].PageNumber);
        Assert.Equal(3, result.Pages[2].PageNumber);
        Assert.Contains("Page one", result.Pages[0].Text);
        Assert.Contains("Page two", result.Pages[1].Text);
        Assert.Contains("Page three", result.Pages[2].Text);
    }

    [Fact]
    public async Task ExtractAsync_PdfWithNoText_ReturnsOcrRequired()
    {
        // PdfPig's writer can produce a page with no text content — the
        // page parses as a structurally valid empty page. This simulates
        // the "scanned image only" case the OcrRequired branch exists
        // for.
        var pdfBytes = BuildEmptyPdf();

        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrRequired, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("OCR fallback", result.Error);
        Assert.Empty(result.Pages);
        Assert.Empty(result.Outline);
    }

    [Fact]
    public async Task ExtractAsync_CancelledToken_PropagatesCancellation()
    {
        var extractor = new PdfPigDocumentTextExtractor(NullLogger<PdfPigDocumentTextExtractor>.Instance);
        using var stream = new MemoryStream(BuildPdfWithText("Some content here for the cancellation test."));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extractor.ExtractAsync(stream, cts.Token));
    }

    // --- Fixture helpers ----------------------------------------------------

    // Build a single-page PDF containing the given text, using PdfPig's
    // writer. Returns the PDF as a byte array suitable for streaming
    // back through the extractor.
    private static byte[] BuildPdfWithText(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(width: 612, height: 792); // US Letter
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, fontSize: 12, position: new(50, 700), font: font);
        return builder.Build();
    }

    private static byte[] BuildPdfWithPages(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(width: 612, height: 792);
            page.AddText(text, fontSize: 12, position: new(50, 700), font: font);
        }
        return builder.Build();
    }

    private static byte[] BuildEmptyPdf()
    {
        // A structurally valid PDF with one page and no text content.
        // This is the synthetic equivalent of a scanned-image-only PDF
        // where PdfPig parses successfully but yields no extractable
        // text — the OcrRequired heuristic branch.
        var builder = new PdfDocumentBuilder();
        builder.AddPage(width: 612, height: 792);
        return builder.Build();
    }
}
