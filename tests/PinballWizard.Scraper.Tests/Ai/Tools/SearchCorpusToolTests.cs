using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai.Tools;

// Behavior-asserting tests for SearchCorpusTool (build-spec § Phase 4
// item 21, ADR-0014 + ADR-0022). The retriever is mocked via NSubstitute
// to keep tests pure; the live integration path is exercised by the
// gated `LiveSearchCorpusToolTests` against a deployed AI Search index.
public sealed class SearchCorpusToolTests
{
    private static SearchCorpusTool NewTool(IRagRetriever retriever) =>
        new(retriever, NullLogger<SearchCorpusTool>.Instance);

    private static RetrievedChunk SampleChunk(
        string chunkId = "chk_1",
        string machineId = "GRBE-MJL05",
        string documentId = "doc_1",
        int pageStart = 1,
        int pageEnd = 1) =>
        new(
            ChunkId: chunkId,
            MachineId: machineId,
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern Pinball",
            DocumentId: documentId,
            DocumentUrl: $"https://example/{documentId}.pdf",
            DocumentType: "manual",
            PageStart: pageStart,
            PageEnd: pageEnd,
            SectionHeading: "Foo Mode",
            Content: "Foo Mode rules text…",
            Score: 0.85);

    [Fact]
    public async Task SearchCorpusAsync_WhitespaceQuery_ReturnsEmptyWithoutCallingRetriever()
    {
        // Empty-query short-circuit prevents the model from looping
        // when a confused prompt edit produces "" — and keeps the
        // counter clean (no retrieval was attempted, not a retrieval
        // that returned zero).
        var retriever = Substitute.For<IRagRetriever>();
        var tool = NewTool(retriever);

        var result = await tool.SearchCorpusAsync(
            query: "   ",
            machineId: null,
            documentType: null,
            topK: null,
            cancellationToken: CancellationToken.None);

        Assert.Empty(result.Hits);
        await retriever.DidNotReceive().RetrieveAsync(
            Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_NullArgs_PassThroughAsUnfilteredRetrieval()
    {
        // The model can omit machineId / documentType / topK; the tool
        // builds RetrievalOptions with defaults so the retriever sees
        // no filter at all.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = NewTool(retriever);
        await tool.SearchCorpusAsync(
            query: "godzilla coil resistance",
            machineId: null,
            documentType: null,
            topK: null,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            "godzilla coil resistance",
            Arg.Is<RetrievalOptions>(o =>
                o.MachineId == null
                && o.DocumentType == null
                && o.TopK == SearchCorpusTool.TopKDefault),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_PassesArgsThroughToRetrievalOptions()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = NewTool(retriever);
        await tool.SearchCorpusAsync(
            query: "service bulletin",
            machineId: "GRBE-MJL05",
            documentType: "service_bulletin",
            topK: 3,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            "service bulletin",
            Arg.Is<RetrievalOptions>(o =>
                o.MachineId == "GRBE-MJL05"
                && o.DocumentType == "service_bulletin"
                && o.TopK == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_EmptyStringFilters_NormalizeToNull()
    {
        // Empty string is "model didn't supply" semantics — must not
        // emit `eq ''` filter clauses that would exclude every legit
        // value. AiSearchRagRetriever.BuildFilter applies the same
        // empty-as-absent rule on its end; both sides agree.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = NewTool(retriever);
        await tool.SearchCorpusAsync(
            query: "x",
            machineId: "  ",
            documentType: "",
            topK: null,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.MachineId == null && o.DocumentType == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, SearchCorpusTool.TopKDefault)]
    [InlineData(0, SearchCorpusTool.TopKDefault)]
    [InlineData(-5, SearchCorpusTool.TopKDefault)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, SearchCorpusTool.TopKCeiling)]
    [InlineData(1000, SearchCorpusTool.TopKCeiling)]
    public void ClampTopK_HonorsCeilingAndDefaults(int? requested, int expected)
    {
        Assert.Equal(expected, SearchCorpusTool.ClampTopK(requested));
    }

    [Fact]
    public async Task SearchCorpusAsync_MapsRetrievedChunksToHits_PreservingFields()
    {
        // The DTO drops Score, ChunkId, Manufacturer (model-facing
        // surface concerns); MachineTitle / DocumentUrl / page range /
        // SectionHeading / Content all flow through unchanged.
        var chunk = SampleChunk(chunkId: "chk_abc", documentId: "doc_x", pageStart: 42, pageEnd: 43);
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([chunk]);

        var tool = NewTool(retriever);
        var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(chunk.MachineId, hit.MachineId);
        Assert.Equal(chunk.MachineTitle, hit.MachineTitle);
        Assert.Equal(chunk.DocumentId, hit.DocumentId);
        Assert.Equal(chunk.DocumentUrl, hit.DocumentUrl);
        Assert.Equal(chunk.DocumentType, hit.DocumentType);
        Assert.Equal(42, hit.PageStart);
        Assert.Equal(43, hit.PageEnd);
        Assert.Equal(chunk.SectionHeading, hit.SectionHeading);
        Assert.Equal(chunk.Content, hit.Content);
    }

    [Fact]
    public async Task SearchCorpusAsync_ReturnsAllChunksFromRetriever_WithoutDedup()
    {
        // De-duplication of citations happens in the citation extractor
        // (one Citation per unique DocumentId); the tool itself returns
        // every chunk so the model can read both as evidence.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([
                SampleChunk(chunkId: "chk_a", documentId: "doc_x"),
                SampleChunk(chunkId: "chk_b", documentId: "doc_x"), // same doc
                SampleChunk(chunkId: "chk_c", documentId: "doc_y"),
            ]);

        var tool = NewTool(retriever);
        var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);

        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task SearchCorpusAsync_RetrieverThrows_ReturnsEmpty_DoesNotPropagate()
    {
        // ADR-0023 negative-consequence #3: tool-side failures must NOT
        // bubble out of the function call. Microsoft Agent Framework
        // would retry on a thrown exception, looping the model. Empty
        // result lets the citation-required guardrail (W4-3) surface
        // a NoCitation refusal cleanly.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ =>
                throw new InvalidOperationException("simulated AI Search outage"));

        var tool = NewTool(retriever);
        var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SearchCorpusAsync_RetrieverCancels_PropagatesCancellation()
    {
        // Cancellation is the caller's intent — must NOT be swallowed.
        // The exception filter in SearchCorpusTool catches
        // OperationCanceledException explicitly to re-throw rather than
        // burying it under "empty result".
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ =>
                throw new OperationCanceledException());

        var tool = NewTool(retriever);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None));
    }

    [Fact]
    public void Ctor_NullRetriever_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchCorpusTool(null!, NullLogger<SearchCorpusTool>.Instance));
    }
}
