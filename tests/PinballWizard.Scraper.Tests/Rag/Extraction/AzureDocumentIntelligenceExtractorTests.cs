using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Extraction;

// Unit tests for AzureDocumentIntelligenceExtractor covering the two
// failure paths that cannot be exercised through FallbackDocumentTextExtractor
// (which stubs the whole ADI extractor). The internal constructor is used to
// inject a mock DocumentIntelligenceClient so no live endpoint is required.
public sealed class AzureDocumentIntelligenceExtractorTests
{
    [Fact]
    public async Task ExtractAsync_AdiReturnsEmptyContent_ReturnsOcrFailed()
    {
        // ADI succeeds structurally (no exception) but returns empty content —
        // the document is unrecoverable; the extractor must return OcrFailed.
        var client = Substitute.For<DocumentIntelligenceClient>();
        var emptyResult = DocumentIntelligenceModelFactory.AnalyzeResult(
            content: string.Empty,
            pages: []);
        var operation = Substitute.For<Operation<AnalyzeResult>>();
        operation.Value.Returns(emptyResult);
        client.AnalyzeDocumentAsync(
                Arg.Any<WaitUntil>(),
                Arg.Any<AnalyzeDocumentOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(operation);

        var extractor = new AzureDocumentIntelligenceExtractor(
            client, NullLogger<AzureDocumentIntelligenceExtractor>.Instance);

        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrFailed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("empty content", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAsync_AdiThrowsRequestFailedException_ReturnsOcrFailed()
    {
        // ADI throws (e.g., throttling, auth error, service unavailable) —
        // the extractor must catch, log, and return OcrFailed. The exception
        // must NOT propagate — it is a per-document failure, not a worker crash.
        var client = Substitute.For<DocumentIntelligenceClient>();
        client.AnalyzeDocumentAsync(
                Arg.Any<WaitUntil>(),
                Arg.Any<AnalyzeDocumentOptions>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException("simulated ADI 503"));

        var extractor = new AzureDocumentIntelligenceExtractor(
            client, NullLogger<AzureDocumentIntelligenceExtractor>.Instance);

        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var result = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(ExtractionStatus.OcrFailed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("simulated ADI 503", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAsync_CancellationRequested_Rethrows()
    {
        // OperationCanceledException must propagate — swallowing it would prevent
        // graceful worker shutdown (the host stop signal would be ignored).
        var client = Substitute.For<DocumentIntelligenceClient>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        client.AnalyzeDocumentAsync(
                Arg.Any<WaitUntil>(),
                Arg.Any<AnalyzeDocumentOptions>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var extractor = new AzureDocumentIntelligenceExtractor(
            client, NullLogger<AzureDocumentIntelligenceExtractor>.Instance);

        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        // TaskCanceledException is a subclass of OperationCanceledException;
        // ThrowsAnyAsync matches both.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extractor.ExtractAsync(stream, cts.Token));
    }

    [Fact]
    public async Task ExtractAsync_NullStream_Throws()
    {
        var client = Substitute.For<DocumentIntelligenceClient>();
        var extractor = new AzureDocumentIntelligenceExtractor(
            client, NullLogger<AzureDocumentIntelligenceExtractor>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => extractor.ExtractAsync(null!, CancellationToken.None));
    }
}
