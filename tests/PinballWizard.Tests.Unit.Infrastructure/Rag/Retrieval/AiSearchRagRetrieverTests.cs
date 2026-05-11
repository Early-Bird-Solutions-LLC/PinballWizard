using Azure.Search.Documents.Models;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Rag.Retrieval;

// Behavior-asserting tests for AiSearchRagRetriever (build-spec §
// Phase 4 item 20, ADR-0021 — index schema, ADR-0022 — citation
// extraction). Each test covers a behavior the build-spec calls out;
// pure-function units (BuildFilter, BuildSearchOptionsCore,
// ResolveScore, MapToChunk) are exercised here. End-to-end integration
// against a deployed pinwiz-rag-v1 index lives in
// AiSearchRagRetrieverLiveTests gated by PINBALL_WIZARD_LIVE_RAG_TESTS.
public sealed class AiSearchRagRetrieverTests
{
    private static readonly ReadOnlyMemory<float> SampleVector =
        new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

    [Fact]
    public void BuildFilter_NoFacets_ReturnsNull()
    {
        var filter = AiSearchRagRetriever.BuildFilter(new RetrievalOptions());

        Assert.Null(filter);
    }

    [Fact]
    public void BuildFilter_OnlyMachineId_ProducesEqualsClause()
    {
        var filter = AiSearchRagRetriever.BuildFilter(new RetrievalOptions(MachineId: "mch_godzilla"));

        Assert.Equal("machine_id eq 'mch_godzilla'", filter);
    }

    [Fact]
    public void BuildFilter_AllThreeFacets_JoinsWithAnd()
    {
        var filter = AiSearchRagRetriever.BuildFilter(new RetrievalOptions(
            MachineId: "mch_godzilla",
            DocumentType: "manual",
            Manufacturer: "Stern Pinball"));

        Assert.Equal(
            "machine_id eq 'mch_godzilla' and document_type eq 'manual' and manufacturer eq 'Stern Pinball'",
            filter);
    }

    [Fact]
    public void BuildFilter_ValueWithSingleQuote_EscapesByDoubling()
    {
        // OData injection guard: a manufacturer or machine title with
        // an apostrophe (e.g. a fan-named machine "Foo's Bar") must
        // round-trip through the escape rule rather than break the
        // filter or inject a clause. ' → ''.
        var filter = AiSearchRagRetriever.BuildFilter(new RetrievalOptions(MachineId: "O'Brien"));

        Assert.Equal("machine_id eq 'O''Brien'", filter);
    }

    [Fact]
    public void BuildFilter_EmptyStringFacet_TreatedAsAbsent()
    {
        var filter = AiSearchRagRetriever.BuildFilter(new RetrievalOptions(
            MachineId: "mch_x",
            DocumentType: ""));

        Assert.Equal("machine_id eq 'mch_x'", filter);
    }

    [Fact]
    public void BuildSearchOptions_SetsSizeAndSemanticConfig()
    {
        var options = AiSearchRagRetriever.BuildSearchOptionsCore(
            SampleVector,
            new RetrievalOptions(TopK: 7),
            "pinwiz-rag-semantic-v1");

        Assert.Equal(7, options.Size);
        Assert.Equal(SearchQueryType.Semantic, options.QueryType);
        Assert.NotNull(options.SemanticSearch);
        Assert.Equal("pinwiz-rag-semantic-v1", options.SemanticSearch!.SemanticConfigurationName);
    }

    [Fact]
    public void BuildSearchOptions_AttachesVectorQueryWithKnnAndContentEmbeddingField()
    {
        var options = AiSearchRagRetriever.BuildSearchOptionsCore(
            SampleVector,
            new RetrievalOptions(TopK: 12),
            "anything");

        Assert.NotNull(options.VectorSearch);
        var vectorQuery = Assert.Single(options.VectorSearch!.Queries);
        var vectorized = Assert.IsType<VectorizedQuery>(vectorQuery);
        Assert.Equal(12, vectorized.KNearestNeighborsCount);
        Assert.Contains("content_embedding", vectorized.Fields);
        Assert.Equal(SampleVector.Length, vectorized.Vector.Length);
    }

    [Fact]
    public void BuildSearchOptions_OmitsFilterWhenNoFacets()
    {
        var options = AiSearchRagRetriever.BuildSearchOptionsCore(
            SampleVector,
            new RetrievalOptions(),
            "anything");

        Assert.Null(options.Filter);
    }

    [Fact]
    public void BuildSearchOptions_AttachesFilterWhenFacetsPresent()
    {
        var options = AiSearchRagRetriever.BuildSearchOptionsCore(
            SampleVector,
            new RetrievalOptions(MachineId: "mch_godzilla", DocumentType: "manual"),
            "anything");

        Assert.Equal("machine_id eq 'mch_godzilla' and document_type eq 'manual'", options.Filter);
    }

    [Fact]
    public void BuildSearchOptions_SelectsCitationFieldsButNotEmbedding()
    {
        // The 3072-d content_embedding column must NOT be projected
        // back — bandwidth and memory cost. Schema fields needed for
        // citation rendering and orchestrator routing must all be
        // selected, including last_scraped_utc (PR-C3).
        var options = AiSearchRagRetriever.BuildSearchOptionsCore(
            SampleVector,
            new RetrievalOptions(),
            "anything");

        Assert.Contains("chunk_id", options.Select);
        Assert.Contains("machine_title", options.Select);
        Assert.Contains("document_url", options.Select);
        Assert.Contains("page_start", options.Select);
        Assert.Contains("section_heading", options.Select);
        Assert.Contains("content", options.Select);
        // PR-C3: last_scraped_utc must be projected so the retriever
        // can thread it through to Citation.LastScrapedUtc.
        Assert.Contains("last_scraped_utc", options.Select);
        Assert.DoesNotContain("content_embedding", options.Select);
    }

    [Fact]
    public void ResolveScore_PrefersRerankerScoreWhenPresent()
    {
        Assert.Equal(0.95, AiSearchRagRetriever.ResolveScore(rerankerScore: 0.95, bm25Score: 8.7));
    }

    [Fact]
    public void ResolveScore_FallsBackToBm25WhenRerankerNull()
    {
        Assert.Equal(8.7, AiSearchRagRetriever.ResolveScore(rerankerScore: null, bm25Score: 8.7));
    }

    [Fact]
    public void ResolveScore_BothNullReturnsZero()
    {
        Assert.Equal(0.0, AiSearchRagRetriever.ResolveScore(rerankerScore: null, bm25Score: null));
    }

    [Fact]
    public void MapToChunk_RoundTripsAllSchemaFields()
    {
        var expectedLastScraped = new DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero);
        var doc = new RetrievedChunkDocument
        {
            ChunkId = "chunk_abc",
            MachineId = "mch_godzilla",
            MachineTitle = "Godzilla (Premium)",
            Manufacturer = "Stern Pinball",
            DocumentId = "doc_godzilla_manual",
            DocumentUrl = "https://sternpinball.com/wp-content/uploads/godzilla_manual.pdf",
            DocumentType = "manual",
            PageStart = 42,
            PageEnd = 43,
            SectionHeading = "Foo Mode Rules",
            Content = "Foo Mode awards the Wizard combo bonus when …",
            LastScrapedUtc = expectedLastScraped,
        };

        var chunk = AiSearchRagRetriever.MapToChunk(doc, score: 0.91);

        Assert.Equal("chunk_abc", chunk.ChunkId);
        Assert.Equal("mch_godzilla", chunk.MachineId);
        Assert.Equal("Godzilla (Premium)", chunk.MachineTitle);
        Assert.Equal("Stern Pinball", chunk.Manufacturer);
        Assert.Equal("doc_godzilla_manual", chunk.DocumentId);
        Assert.Equal("https://sternpinball.com/wp-content/uploads/godzilla_manual.pdf", chunk.DocumentUrl);
        Assert.Equal("manual", chunk.DocumentType);
        Assert.Equal(42, chunk.PageStart);
        Assert.Equal(43, chunk.PageEnd);
        Assert.Equal("Foo Mode Rules", chunk.SectionHeading);
        Assert.Equal("Foo Mode awards the Wizard combo bonus when …", chunk.Content);
        Assert.Equal(0.91, chunk.Score);
        // PR-C3: LastScrapedUtc must round-trip from RetrievedChunkDocument.
        Assert.Equal(expectedLastScraped, chunk.LastScrapedUtc);
    }

    [Fact]
    public void MapToChunk_NullLastScrapedUtc_PropagatesNull()
    {
        // Pre-C3 chunks and chunks from scrapers that didn't populate
        // Timeline.LastDownloadedAt will have null here. The retriever
        // must not throw and must propagate null so the citation extractor
        // can render the freshness badge conditionally.
        var doc = new RetrievedChunkDocument
        {
            ChunkId = "chunk_abc",
            MachineId = "mch_x",
            DocumentUrl = "https://example/doc.pdf",
            LastScrapedUtc = null,
        };

        var chunk = AiSearchRagRetriever.MapToChunk(doc, score: 0.5);

        Assert.Null(chunk.LastScrapedUtc);
    }
}
