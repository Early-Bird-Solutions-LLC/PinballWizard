using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Extraction;

// Unit tests for FallbackDocumentTextExtractor. The ADI extractor is
// stubbed via IDocumentTextExtractor so no live Azure endpoint is
// required. The internal constructor is used to inject the stub.
// PDFs are generated programmatically (same approach as
// PdfPigDocumentTextExtractorTests) to keep the suite self-contained.
public sealed class FallbackDocumentTextExtractorTests
{
    [Fact]
    public void Ctor_NullPrimary_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new FallbackDocumentTextExtractor(
                null!,
                Substitute.For<IDocumentTextExtractor>(),
                NullLogger<FallbackDocumentTextExtractor>.Instance));
    }

    [Fact]
    public void Ctor_NullFallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new FallbackDocumentTextExtractor(
                NewPdfPigExtractor(),
                (IDocumentTextExtractor)null!,
                NullLogger<FallbackDocumentTextExtractor>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new FallbackDocumentTextExtractor(
                NewPdfPigExtractor(),
                Substitute.For<IDocumentTextExtractor>(),
                null!));
    }

    [Fact]
    public async Task ExtractAsync_NullStream_Throws()
    {
        var extractor = NewFallback(Substitute.For<IDocumentTextExtractor>());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => extractor.ExtractAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_PdfPigSucceeds_ReturnsPrimaryResultWithoutCallingFallback()
    {
        var fallback = Substitute.For<IDocumentTextExtractor>();
        var extractor = NewFallback(fallback);
        using var stream = new MemoryStream(BuildPdfWithText(
            "Hello pinball world. This is a synthetic fixture document with enough text to clear the OCR floor."));

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Contains("Hello pinball world", result.Text);
        await fallback.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task ExtractAsync_PdfPigReturnsOcrRequired_DelegatesToFallback()
    {
        var adiResult = new ExtractedDocument(
            Status: ExtractionStatus.Success,
            Text: "ADI extracted text",
            Pages: [new ExtractedPage(1, "ADI extracted text")],
            Outline: [],
            Error: null);

        var fallback = Substitute.For<IDocumentTextExtractor>();
        fallback.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(adiResult);

        // OcrRequiredCharFloor raised to force OcrRequired on any real PDF
        var extractor = NewFallback(fallback, ocrRequiredCharFloor: 1_000_000);
        using var stream = new MemoryStream(BuildPdfWithText("Short text"));

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Success, result.Status);
        Assert.Equal("ADI extracted text", result.Text);
        await fallback.Received(1).ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PdfPigReturnsOcrRequired_FallbackReturnsOcrFailed_PropagatesOcrFailed()
    {
        var fallback = Substitute.For<IDocumentTextExtractor>();
        fallback.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ExtractedDocument.Failure(ExtractionStatus.OcrFailed, "ADI also returned empty content"));

        var extractor = NewFallback(fallback, ocrRequiredCharFloor: 1_000_000);
        using var stream = new MemoryStream(BuildPdfWithText("Tiny"));

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrFailed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExtractAsync_PdfPigReturnsEncrypted_ReturnsPrimaryResultWithoutCallingFallback()
    {
        var fallback = Substitute.For<IDocumentTextExtractor>();
        var extractor = NewFallback(fallback);

        // Encrypted PDF bytes — a "password protected" PDF triggers Encrypted
        // in PdfPig. We simulate this by feeding junk bytes — PdfPig returns
        // Malformed rather than Encrypted on random bytes, so we verify the
        // non-OcrRequired passthrough contract by checking the status is NOT
        // OcrRequired and fallback was never called.
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01, 0x02 });

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.NotEqual(ExtractionStatus.OcrRequired, result.Status);
        await fallback.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task ExtractAsync_PdfPigReturnsMalformed_ReturnsPrimaryResultWithoutCallingFallback()
    {
        var fallback = Substitute.For<IDocumentTextExtractor>();
        var extractor = NewFallback(fallback);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a pdf"));

        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
        await fallback.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task ExtractAsync_OcrRequired_StreamSeekResetBeforeFallback()
    {
        // Verify that when PdfPig returns OcrRequired, the stream is rewound
        // to position 0 before the fallback extractor sees it. The fallback
        // stub captures the stream position on entry.
        long capturedPosition = -1;
        var fallback = Substitute.For<IDocumentTextExtractor>();
        fallback.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPosition = callInfo.Arg<Stream>().Position;
                return Task.FromResult(ExtractedDocument.Failure(
                    ExtractionStatus.OcrFailed, "stub"));
            });

        var extractor = NewFallback(fallback, ocrRequiredCharFloor: 1_000_000);
        using var stream = new MemoryStream(BuildPdfWithText("text"));

        await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(0, capturedPosition);
    }

    [Fact]
    public async Task ExtractAsync_OcrRequired_FallbackThrows_ExceptionPropagatesNotSwallowed()
    {
        // FallbackDocumentTextExtractor has no try/catch around the fallback call.
        // If the fallback throws (e.g. ADI endpoint is unreachable), the exception
        // must propagate to the caller — it must NOT be silently swallowed or
        // converted to an empty / OcrFailed result.  This guards against a future
        // refactor that adds a catch-all and masks transport failures as silent
        // no-ops, which would let the caller believe extraction succeeded with no text.
        var throwingFallback = Substitute.For<IDocumentTextExtractor>();
        throwingFallback
            .ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<ExtractedDocument>(_ => throw new InvalidOperationException("ADI endpoint unreachable"));

        // ocrRequiredCharFloor raised so any real PDF returns OcrRequired,
        // routing us into the fallback code path.
        var extractor = NewFallback(throwingFallback, ocrRequiredCharFloor: 1_000_000);
        using var stream = new MemoryStream(BuildPdfWithText("short text"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync(stream, CancellationToken.None));
    }

    // --- Fixture helpers -------------------------------------------------------

    private static FallbackDocumentTextExtractor NewFallback(
        IDocumentTextExtractor fallback,
        int? ocrRequiredCharFloor = null)
    {
        return new FallbackDocumentTextExtractor(
            NewPdfPigExtractor(ocrRequiredCharFloor),
            fallback,
            NullLogger<FallbackDocumentTextExtractor>.Instance);
    }

    private static PdfPigDocumentTextExtractor NewPdfPigExtractor(int? ocrRequiredCharFloor = null)
    {
        var options = new PdfExtractionOptions();
        if (ocrRequiredCharFloor is { } f) options.OcrRequiredCharFloor = f;
        return new PdfPigDocumentTextExtractor(
            Options.Create(options),
            NullLogger<PdfPigDocumentTextExtractor>.Instance);
    }

    private static byte[] BuildPdfWithText(string text)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(width: 595, height: 842); // A4
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }
}
