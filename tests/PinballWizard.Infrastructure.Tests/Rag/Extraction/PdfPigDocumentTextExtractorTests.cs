using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Extraction;

// Unit tests for PdfPigDocumentTextExtractor. Fixture PDFs are generated
// programmatically via PdfPig's own writer (UglyToad.PdfPig.Writer)
// rather than committed as binary blobs — keeps the test suite
// self-contained and deterministic. Per the build-spec § Phase 4 scope
// item 14, fixture coverage spans success, scanned-image-only
// (OcrRequired), malformed input, and basic outline extraction.
public sealed class PdfPigDocumentTextExtractorTests
{
    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PdfPigDocumentTextExtractor(null!, NullLogger<PdfPigDocumentTextExtractor>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PdfPigDocumentTextExtractor(Options.Create(new PdfExtractionOptions()), null!));
    }

    [Fact]
    public async Task ExtractAsync_NullStream_Throws()
    {
        var extractor = NewExtractor();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => extractor.ExtractAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_MalformedBytes_ReturnsMalformedStatus()
    {
        var extractor = NewExtractor();
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
        var extractor = NewExtractor();
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

        var extractor = NewExtractor();
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

        var extractor = NewExtractor();
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

        var extractor = NewExtractor();
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrRequired, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("OCR fallback", result.Error);
        Assert.Empty(result.Pages);
        Assert.Empty(result.Outline);
    }

    [Fact]
    public async Task ExtractAsync_StreamLargerThanMaxStreamBytes_ReturnsSizeExceeded()
    {
        // Build a PDF small enough to be valid, but with options.MaxStreamBytes
        // set BELOW its actual length — the size guard must reject it
        // pre-parse rather than attempting to open and ballooning memory.
        var pdfBytes = BuildPdfWithText("Some content; the test only cares about stream length.");
        var extractor = NewExtractor(maxStreamBytes: pdfBytes.Length - 1);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.SizeExceeded, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("MaxStreamBytes", result.Error);
        Assert.Empty(result.Pages);
        Assert.Empty(result.Outline);
    }

    [Fact]
    public async Task ExtractAsync_StreamWithinMaxStreamBytes_ProcessesNormally()
    {
        // Mirror of the rejection test above but with options.MaxStreamBytes
        // set ABOVE the stream length — confirms the size guard doesn't
        // false-positive on legitimately-sized PDFs.
        var pdfBytes = BuildPdfWithText("Hello pinball — this is a synthetic fixture document with enough text to clear the OCR-required floor heuristic.");
        var extractor = NewExtractor(maxStreamBytes: pdfBytes.Length * 10);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
    }

    [Fact]
    public async Task ExtractAsync_NonSeekableStream_SkipsSizeCheckAndFallsThroughToParser()
    {
        // PdfPig requires a seekable stream for cross-reference parsing,
        // so a non-seekable stream is expected to fail somewhere
        // downstream. The size guard is documented to skip the check
        // when CanSeek=false (the parser will surface the issue
        // anyway). Pin this contract so a future change that adds a
        // pre-emptive non-seekable rejection is conscious.
        var pdfBytes = BuildPdfWithText("Hello pinball.");
        var extractor = NewExtractor(maxStreamBytes: 1); // tiny, would reject if it COULD measure
        using var inner = new MemoryStream(pdfBytes);
        using var nonSeekable = new NonSeekableStream(inner);

        var result = await extractor.ExtractAsync(nonSeekable, CancellationToken.None);

        // The size guard does NOT fire (CanSeek=false). The parser
        // surfaces a Malformed status because PdfPig can't seek to
        // parse the cross-reference table.
        Assert.NotEqual(ExtractionStatus.SizeExceeded, result.Status);
    }

    [Fact]
    public async Task ExtractAsync_OcrRequiredCharFloorRaised_ClassifiesShortDocAsOcrRequired()
    {
        // Pin the options-driven OCR floor: with the floor raised above
        // the document's char count, even a structurally valid text PDF
        // routes to OcrRequired. Confirms the option is read (no dead
        // config) and the threshold is genuinely tunable.
        var pdfBytes = BuildPdfWithText("Tiny document that exceeds default 32-char floor easily.");
        var extractor = NewExtractor(ocrRequiredCharFloor: 10000);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrRequired, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExtractAsync_CancelledToken_PropagatesCancellation()
    {
        var extractor = NewExtractor();
        using var stream = new MemoryStream(BuildPdfWithText("Some content here for the cancellation test."));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extractor.ExtractAsync(stream, cts.Token));
    }

    // --- ExtractPreviewAsync (#832) -------------------------------------------

    [Fact]
    public async Task ExtractPreviewAsync_ThreePagePdf_ReturnsExactlyTwoPages()
    {
        var pdfBytes = BuildPdfWithPages("Page one text about Godzilla.", "Page two text.", "Page three text.");
        var extractor = NewExtractor();
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(2, result.Pages[1].PageNumber);
        Assert.Contains("Godzilla", result.Pages[0].Text);
    }

    [Fact]
    public async Task ExtractPreviewAsync_SinglePagePdf_ReturnsOnePage_NotAnError()
    {
        var pdfBytes = BuildPdfWithText("Only page.");
        var extractor = NewExtractor();
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Single(result.Pages);
    }

    [Fact]
    public async Task ExtractPreviewAsync_OversizeStream_ReturnsSizeExceededWithoutParsing()
    {
        var pdfBytes = BuildPdfWithText("Content irrelevant; the guard fires on stream length.");
        var extractor = NewExtractor(maxStreamBytes: 16);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.SizeExceeded, result.Status);
        Assert.Contains("MaxStreamBytes", result.Error);
    }

    [Fact]
    public async Task ExtractPreviewAsync_GarbageBytes_ReturnsMalformed()
    {
        var extractor = NewExtractor();
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task ExtractPreviewAsync_NearEmptyText_ReturnsSuccess_NeverOcrRequired()
    {
        // The preview path deliberately skips the OcrRequiredCharFloor heuristic
        // (spec Section B): an empty preview yields no linking evidence and the
        // page tier declines — that is the honest outcome. OcrRequired belongs
        // to the indexing path only.
        var pdfBytes = BuildPdfWithText("x");
        var extractor = NewExtractor(ocrRequiredCharFloor: 10000);
        using var stream = new MemoryStream(pdfBytes);

        var result = await extractor.ExtractPreviewAsync(stream, pageCount: 2, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
    }

    [Fact]
    public async Task ExtractPreviewAsync_PageCountBelowOne_Throws()
    {
        var extractor = NewExtractor();
        using var stream = new MemoryStream(BuildPdfWithText("content"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => extractor.ExtractPreviewAsync(stream, pageCount: 0, CancellationToken.None));
    }

    // --- Fixture helpers ----------------------------------------------------

    private static PdfPigDocumentTextExtractor NewExtractor(
        long? maxStreamBytes = null,
        int? ocrRequiredCharFloor = null)
    {
        var options = new PdfExtractionOptions();
        if (maxStreamBytes is { } m) options.MaxStreamBytes = m;
        if (ocrRequiredCharFloor is { } f) options.OcrRequiredCharFloor = f;
        return new PdfPigDocumentTextExtractor(
            Options.Create(options),
            NullLogger<PdfPigDocumentTextExtractor>.Instance);
    }

    // Wraps a Stream and reports CanSeek=false. Used to exercise the
    // size-guard's "skip when not seekable" branch — MemoryStream
    // itself is always seekable, so we need a wrapper to simulate the
    // non-seekable case (a network stream, for instance).
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();
    }


    // Build a single-page PDF containing the given text, using PdfPig's
    // writer. Returns the PDF as a byte array suitable for streaming
    // back through the extractor.
    private static byte[] BuildPdfWithText(string text)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(width: 612, height: 792); // US Letter
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, fontSize: 12, position: new(50, 700), font: font);
        return builder.Build();
    }

    private static byte[] BuildPdfWithPages(params string[] pageTexts)
    {
        using var builder = new PdfDocumentBuilder();
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
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(width: 612, height: 792);
        return builder.Build();
    }
}
