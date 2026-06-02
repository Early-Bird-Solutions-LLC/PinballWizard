using Azure.Search.Documents.Indexes.Models;
using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavior-asserting tests for the v1 RAG index schema (ADR-0021).
// Each test pins a specific row of ADR-0021 § Schema, the vector
// configuration block, or the semantic-ranker block — adding a field
// to the index requires either updating one of these tests or
// adding a new one. Drift here means the live index and the ADR
// disagree; that's a 🔴 in /local-review.
public sealed class AiSearchIndexSchemaTests
{
    private const string IndexName = "pinwiz-rag-v1";
    private const string SemanticConfigName = "pinwiz-rag-semantic-v1";

    private static SearchIndex Build() =>
        AiSearchIndexSchema.Build(IndexName, SemanticConfigName);

    [Fact]
    public void Build_NullName_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace surfaces null as
        // ArgumentNullException (subclass) — use ThrowsAny to cover
        // both the null and whitespace branches under one type
        // umbrella.
        Assert.ThrowsAny<ArgumentException>(() =>
            AiSearchIndexSchema.Build(indexName: null!, SemanticConfigName));
    }

    [Fact]
    public void Build_EmptySemanticConfig_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AiSearchIndexSchema.Build(IndexName, semanticConfigName: "   "));
    }

    [Fact]
    public void Build_PreservesIndexName()
    {
        var index = Build();
        Assert.Equal(IndexName, index.Name);
    }

    [Fact]
    public void Build_DeclaresAllAdrSchemaFields()
    {
        var index = Build();
        var fieldNames = index.Fields.Select(f => f.Name).ToHashSet();

        // Mirror ADR-0021 § Schema row-by-row. Adding a row to the
        // ADR table without updating this assertion means new field
        // exists in code but not in the schema lock-test.
        Assert.Contains("chunk_id", fieldNames);
        Assert.Contains("machine_id", fieldNames);
        Assert.Contains("machine_title", fieldNames);
        Assert.Contains("manufacturer", fieldNames);
        Assert.Contains("document_id", fieldNames);
        Assert.Contains("document_url", fieldNames);
        Assert.Contains("document_type", fieldNames);
        Assert.Contains("page_start", fieldNames);
        Assert.Contains("page_end", fieldNames);
        Assert.Contains("section_heading", fieldNames);
        Assert.Contains("content", fieldNames);
        Assert.Contains("content_embedding", fieldNames);
        // PR-C3: freshness field added to ADR-0021 schema table.
        Assert.Contains("last_scraped_utc", fieldNames);
        // Task 6 (AB#259): edition + edition_scope threaded per chunk.
        Assert.Contains("edition", fieldNames);
        Assert.Contains("edition_scope", fieldNames);
    }

    [Fact]
    public void Build_EditionAndEditionScopeAreFilterableFacetableStrings()
    {
        // Task 6 (AB#259): edition (free-text label e.g. "Pro") and
        // edition_scope (single-edition / edition-subset / franchise-wide)
        // are String fields, filterable + facetable so a future retriever
        // query can filter chunks by edition scope. Mirrors the machine_id
        // field's filter/facet flags.
        var index = Build();

        var edition = index.Fields.Single(f => f.Name == "edition");
        Assert.Equal(SearchFieldDataType.String, edition.Type);
        Assert.True(edition.IsFilterable);
        Assert.True(edition.IsFacetable);

        var editionScope = index.Fields.Single(f => f.Name == "edition_scope");
        Assert.Equal(SearchFieldDataType.String, editionScope.Type);
        Assert.True(editionScope.IsFilterable);
        Assert.True(editionScope.IsFacetable);
    }

    [Fact]
    public void Build_ChunkIdIsKey()
    {
        var index = Build();
        var chunkId = index.Fields.Single(f => f.Name == "chunk_id");
        Assert.True(chunkId.IsKey);
        Assert.True(chunkId.IsFilterable);
    }

    [Fact]
    public void Build_MachineTitleIsSearchableAndSortable()
    {
        var index = Build();
        var field = index.Fields.Single(f => f.Name == "machine_title");
        Assert.True(field.IsSearchable);
        Assert.True(field.IsFilterable);
        Assert.True(field.IsFacetable);
        Assert.True(field.IsSortable);
    }

    [Fact]
    public void Build_ContentIsSearchableButNotFilterable()
    {
        var index = Build();
        var content = index.Fields.Single(f => f.Name == "content");
        Assert.True(content.IsSearchable);
        Assert.NotEqual(true, content.IsFilterable); // null or false both acceptable
    }

    [Fact]
    public void Build_DocumentTypeAndManufacturerAreFacetable()
    {
        var index = Build();
        var docType = index.Fields.Single(f => f.Name == "document_type");
        Assert.True(docType.IsFilterable);
        Assert.True(docType.IsFacetable);

        var mfg = index.Fields.Single(f => f.Name == "manufacturer");
        Assert.True(mfg.IsFilterable);
        Assert.True(mfg.IsFacetable);
    }

    [Fact]
    public void Build_PageStartAndPageEndAreInt32AndSortable()
    {
        var index = Build();
        var pageStart = index.Fields.Single(f => f.Name == "page_start");
        var pageEnd = index.Fields.Single(f => f.Name == "page_end");
        Assert.Equal(SearchFieldDataType.Int32, pageStart.Type);
        Assert.Equal(SearchFieldDataType.Int32, pageEnd.Type);
        Assert.True(pageStart.IsSortable);
        Assert.True(pageEnd.IsSortable);
    }

    [Fact]
    public void Build_ContentEmbeddingHasCorrectDimensionsAndProfile()
    {
        var index = Build();
        var embedding = index.Fields.Single(f => f.Name == "content_embedding");

        // ADR-0021 + ADR-0020 lock 3072d. Changing this is a v1→v2
        // schema cutover, not an in-place update.
        Assert.Equal(AiSearchIndexSchema.EmbeddingDimensions, embedding.VectorSearchDimensions);
        Assert.Equal(3072, embedding.VectorSearchDimensions);
        Assert.Equal(AiSearchIndexSchema.VectorProfileName, embedding.VectorSearchProfileName);
    }

    [Fact]
    public void Build_LastScrapedUtcIsDateTimeOffsetFilterableSortable()
    {
        // PR-C3: last_scraped_utc is DateTimeOffset, filterable + sortable
        // so freshness-sort queries work. NOT searchable (timestamps are
        // opaque to the text-search engine) and NOT facetable (continuous
        // timestamp). Mirrors the ADR-0021 schema table row added in PR-C3.
        var index = Build();
        var field = index.Fields.Single(f => f.Name == "last_scraped_utc");
        Assert.Equal(SearchFieldDataType.DateTimeOffset, field.Type);
        Assert.True(field.IsFilterable);
        Assert.True(field.IsSortable);
        Assert.NotEqual(true, field.IsSearchable);
        Assert.NotEqual(true, field.IsFacetable);
    }

    [Fact]
    public void Build_VectorSearchHasHnswCosineProfile()
    {
        var index = Build();
        Assert.NotNull(index.VectorSearch);

        var profile = Assert.Single(index.VectorSearch!.Profiles);
        Assert.Equal(AiSearchIndexSchema.VectorProfileName, profile.Name);
        Assert.Equal(AiSearchIndexSchema.HnswAlgorithmConfigName, profile.AlgorithmConfigurationName);

        var algo = Assert.Single(index.VectorSearch.Algorithms);
        Assert.Equal(AiSearchIndexSchema.HnswAlgorithmConfigName, algo.Name);
        var hnsw = Assert.IsType<HnswAlgorithmConfiguration>(algo);
        Assert.Equal(VectorSearchAlgorithmMetric.Cosine, hnsw.Parameters?.Metric);
    }

    [Fact]
    public void Build_SemanticConfigurationMatchesAdr()
    {
        var index = Build();
        Assert.NotNull(index.SemanticSearch);

        var semantic = Assert.Single(index.SemanticSearch!.Configurations);
        Assert.Equal(SemanticConfigName, semantic.Name);

        // ADR-0021 § Semantic ranker:
        //   title_field = section_heading
        //   prioritized_content_fields = [content]
        //   prioritized_keyword_fields = [machine_title, section_heading]
        Assert.Equal("section_heading", semantic.PrioritizedFields.TitleField?.FieldName);

        var contentNames = semantic.PrioritizedFields.ContentFields.Select(f => f.FieldName).ToList();
        Assert.Equal(["content"], contentNames);

        var keywordNames = semantic.PrioritizedFields.KeywordsFields.Select(f => f.FieldName).ToList();
        Assert.Equal(["machine_title", "section_heading"], keywordNames);
    }
}
