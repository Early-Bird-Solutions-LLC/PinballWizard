using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Retrieval;

// Tests for the AiSearchRagRetriever reranker integration path
// (ADR-0024 W4 fix-up). Verifies that ApplyRerankingAsync correctly
// maps RankedChunk results back to RetrievedChunk with the Cohere
// RelevanceScore replacing the original AI Search score.
public sealed class AiSearchRagRetrieverRerankerTests
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
            Content: $"content for {id}",
            Score: score);

    [Fact]
    public async Task ApplyRerankingAsync_WithReranker_ReplacesScoresAndReordersChunks()
    {
        // Arrange: reranker returns chunks in reverse order with new scores.
        var query = "What is Kaiju multiball?";
        var chunkA = MakeChunk("chunk_A", score: 0.9);
        var chunkB = MakeChunk("chunk_B", score: 0.8);
        var candidates = new[] { chunkA, chunkB };

        var reranker = Substitute.For<ICrossEncoderReranker>();
        reranker.RerankAsync(query, candidates, topN: 5, CancellationToken.None)
                .Returns(Task.FromResult<IReadOnlyList<RankedChunk>>(new[]
                {
                    new RankedChunk(chunkB, RelevanceScore: 0.95f),   // B ranked higher by Cohere
                    new RankedChunk(chunkA, RelevanceScore: 0.60f),
                }));

        // Act
        var result = await AiSearchRagRetriever.ApplyRerankingAsync(
            query, candidates, topN: 5, reranker, CancellationToken.None);

        // Assert: order follows Cohere ranking; Score replaced with RelevanceScore.
        Assert.Equal(2, result.Count);
        Assert.Equal("chunk_B", result[0].ChunkId);
        Assert.Equal(0.95, result[0].Score, precision: 3);
        Assert.Equal("chunk_A", result[1].ChunkId);
        Assert.Equal(0.60, result[1].Score, precision: 3);
    }

    [Fact]
    public async Task ApplyRerankingAsync_PreservesAllOtherChunkFields()
    {
        // The reranker must not drop citation fields (DocumentUrl, PageStart, etc.)
        // because they feed the citation surface downstream.
        var query = "query";
        var original = new RetrievedChunk(
            ChunkId: "chunk_Z",
            MachineId: "mch_godzilla",
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern Pinball",
            DocumentId: "doc_godzilla",
            DocumentUrl: "https://sternpinball.com/godzilla.pdf",
            DocumentType: "manual",
            PageStart: 42,
            PageEnd: 43,
            SectionHeading: "Kaiju Mode",
            Content: "some content",
            Score: 0.7,
            LastScrapedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var reranker = Substitute.For<ICrossEncoderReranker>();
        reranker.RerankAsync(query, Arg.Any<IReadOnlyList<RetrievedChunk>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<RankedChunk>>(new[]
                {
                    new RankedChunk(original, RelevanceScore: 0.88f),
                }));

        var result = await AiSearchRagRetriever.ApplyRerankingAsync(
            query, new[] { original }, topN: 5, reranker, CancellationToken.None);

        var reranked = Assert.Single(result);
        Assert.Equal("mch_godzilla", reranked.MachineId);
        Assert.Equal("Godzilla (Premium)", reranked.MachineTitle);
        Assert.Equal("https://sternpinball.com/godzilla.pdf", reranked.DocumentUrl);
        Assert.Equal(42, reranked.PageStart);
        Assert.Equal("Kaiju Mode", reranked.SectionHeading);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), reranked.LastScrapedUtc);
        // Score replaced with Cohere RelevanceScore (cast to double).
        Assert.Equal(0.88, reranked.Score, precision: 3);
    }
}
