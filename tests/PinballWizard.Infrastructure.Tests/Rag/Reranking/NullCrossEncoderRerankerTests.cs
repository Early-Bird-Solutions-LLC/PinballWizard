using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Infrastructure.Rag.Reranking;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Reranking;

// Behaviour tests for NullCrossEncoderReranker — the passthrough
// implementation used when Rag:CrossEncoder:Enabled=false (ADR-0024).
public sealed class NullCrossEncoderRerankerTests
{
    private static RetrievedChunk MakeChunk(string id, double score = 0.5) =>
        new(ChunkId: id,
            MachineId: "mch_x",
            MachineTitle: "Test Machine",
            Manufacturer: "Test Co",
            DocumentId: "doc_x",
            DocumentUrl: "https://example.com/manual.pdf",
            DocumentType: "manual",
            PageStart: 1, PageEnd: 1,
            SectionHeading: "Section",
            Content: "content",
            Score: score);

    [Fact]
    public async Task RerankAsync_ReturnsFirstTopNChunks_WithScorePreserved()
    {
        var chunks = new[]
        {
            MakeChunk("chunk_001", score: 0.9),
            MakeChunk("chunk_002", score: 0.8),
            MakeChunk("chunk_003", score: 0.7),
            MakeChunk("chunk_004", score: 0.6),
        };
        var sut = new NullCrossEncoderReranker();

        var result = await sut.RerankAsync("some query", chunks, topN: 2, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("chunk_001", result[0].Chunk.ChunkId);
        Assert.Equal("chunk_002", result[1].Chunk.ChunkId);
        // NullCrossEncoderReranker uses the existing Score as RelevanceScore
        // so the ordering contract is visible in telemetry without Cohere.
        Assert.Equal((float)0.9, result[0].RelevanceScore);
        Assert.Equal((float)0.8, result[1].RelevanceScore);
    }

    [Fact]
    public async Task RerankAsync_TopNLargerThanCandidates_ReturnsAllCandidates()
    {
        var chunks = new[] { MakeChunk("chunk_001"), MakeChunk("chunk_002") };
        var sut = new NullCrossEncoderReranker();

        var result = await sut.RerankAsync("query", chunks, topN: 99, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task RerankAsync_EmptyCandidates_ReturnsEmpty()
    {
        var sut = new NullCrossEncoderReranker();

        var result = await sut.RerankAsync("query", [], topN: 5, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task RerankAsync_NeverCallsExternalService()
    {
        // NullCrossEncoderReranker must be synchronous-equivalent — no
        // await on any I/O path. Verify by completing without any delay
        // and without needing a real Cohere endpoint configured.
        var sut = new NullCrossEncoderReranker();
        var chunks = new[] { MakeChunk("chunk_001") };

        // Should complete synchronously (ValueTask.IsCompleted immediately).
        var task = sut.RerankAsync("query", chunks, topN: 1, CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
        var result = await task;
        Assert.Single(result);
    }
}
