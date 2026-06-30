using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Retrieval;

// Behavior tests for RetrievalRankProbe (Task 2, reranker-sensitive hard eval).
// Uses a fake IRagRetriever returning a fixed chunk order so tests are
// reranker-agnostic; the probe is defined to run with Rag:CrossEncoder:Enabled=false
// at call time, enforced by the operator/CLI rather than the probe itself.
// Tests exercise REAL mch_-prefixed citation projection so the matching logic
// is not stubbed out — changing the projection in the probe will surface here.
public sealed class RetrievalRankProbeTests
{
    private static RetrievedChunk MakeChunk(string machineId, double score = 0.5) =>
        new(ChunkId: $"chk_{machineId}",
            MachineId: machineId,
            MachineTitle: $"Machine {machineId}",
            Manufacturer: "Test Pinball Co",
            DocumentId: $"doc_{machineId}",
            DocumentUrl: $"https://example.com/{machineId}.pdf",
            DocumentType: "manual",
            PageStart: 1,
            PageEnd: 2,
            SectionHeading: "Rules",
            Content: $"Content about {machineId}",
            Score: score);

    private static EvalQuestion MakeQuestion(IReadOnlyList<string> expectedCitationSet) =>
        new(Id: "probe-test-001",
            Question: "What is the wizard mode?",
            ExpectedSubAgent: "Rules",
            ExpectedCitationSet: expectedCitationSet,
            AcceptableRefusal: false);

    [Fact]
    public async Task ProbeAsync_GoldChunkSeventh_ClassifiesRerankerSensitive()
    {
        // Arrange: 10 chunks; gold is at position 7 (1-based). topN=5, so
        // ranks 6–10 are "reranker-sensitive" (between topN+1 and topK).
        var chunks = Enumerable.Range(1, 10)
            .Select(i => MakeChunk(
                machineId: i == 7 ? "GOLD" : $"other{i}",
                score: 1.0 - i * 0.05))
            .ToList();
        var probe = new RetrievalRankProbe(new FakeRetriever(chunks));
        var q = MakeQuestion(expectedCitationSet: ["mch_GOLD"]);

        var result = await probe.ProbeAsync(q, topN: 5, CancellationToken.None);

        Assert.Equal(7, result.GoldRank);
        Assert.Equal("reranker-sensitive", result.Slice);
    }

    [Fact]
    public async Task ProbeAsync_GoldChunkThird_ClassifiesEasy()
    {
        // Arrange: 10 chunks; gold is at position 3 (1-based). topN=5, so
        // rank 3 <= topN — this is "easy" (already surfaced by first-stage).
        var chunks = Enumerable.Range(1, 10)
            .Select(i => MakeChunk(
                machineId: i == 3 ? "GOLD" : $"other{i}",
                score: 1.0 - i * 0.05))
            .ToList();
        var probe = new RetrievalRankProbe(new FakeRetriever(chunks));
        var q = MakeQuestion(expectedCitationSet: ["mch_GOLD"]);

        var result = await probe.ProbeAsync(q, topN: 5, CancellationToken.None);

        Assert.Equal(3, result.GoldRank);
        Assert.Equal("easy", result.Slice);
    }

    [Fact]
    public async Task ProbeAsync_GoldChunkAbsent_ClassifiesRetrievalMiss()
    {
        // Arrange: 10 chunks; none matches the gold citation. The probe
        // returns GoldRank=null and Slice="retrieval-miss".
        var chunks = Enumerable.Range(1, 10)
            .Select(i => MakeChunk(machineId: $"other{i}", score: 1.0 - i * 0.05))
            .ToList();
        var probe = new RetrievalRankProbe(new FakeRetriever(chunks));
        var q = MakeQuestion(expectedCitationSet: ["mch_GOLD"]);

        var result = await probe.ProbeAsync(q, topN: 5, CancellationToken.None);

        Assert.Null(result.GoldRank);
        Assert.Equal("retrieval-miss", result.Slice);
    }

    private sealed class FakeRetriever(IReadOnlyList<RetrievedChunk> chunks) : IRagRetriever
    {
        public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
            string queryText,
            RetrievalOptions options,
            CancellationToken cancellationToken)
            => Task.FromResult(chunks);
    }
}
