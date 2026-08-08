using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinballWizard.Processor.Indexing;

public sealed class SearchIndexManager
{
    private readonly SearchIndexClient _indexClient;
    private readonly ProcessorSettings _settings;
    private readonly ILogger<SearchIndexManager> _logger;

    public SearchIndexManager(
        SearchIndexClient indexClient,
        IOptions<ProcessorSettings> settings,
        ILogger<SearchIndexManager> logger)
    {
        _indexClient = indexClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task CreateOrUpdateIndexAsync(CancellationToken ct = default)
    {
        var index = BuildIndex();
        _logger.LogInformation("Creating or updating search index '{IndexName}'", _settings.SearchIndexName);
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
        _logger.LogInformation("Search index '{IndexName}' created/updated successfully", _settings.SearchIndexName);
    }

    private SearchIndex BuildIndex()
    {
        var fields = new List<SearchField>
        {
            new SimpleField("chunkId", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SearchableField("content") { AnalyzerName = LexicalAnalyzerName.EnLucene },
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = 1536,
                VectorSearchProfileName = "vector-profile"
            },
            new SimpleField("parentDocId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("gameSlug", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SearchableField("gameTitle") { IsFilterable = true },
            new SimpleField("manufacturer", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("documentType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("sourceType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("sourceUrl", SearchFieldDataType.String),
            new SimpleField("sourceName", SearchFieldDataType.String),
            new SearchableField("sectionPath"),
            new SimpleField("pageNumber", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
            new SimpleField("contentCategories", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true, IsFacetable = true },
            new SimpleField("lastUpdated", SearchFieldDataType.DateTimeOffset) { IsSortable = true }
        };

        var index = new SearchIndex(_settings.SearchIndexName, fields);

        // Configure vector search
        index.VectorSearch = new VectorSearch
        {
            Profiles = { new VectorSearchProfile("vector-profile", "hnsw-config") },
            Algorithms = { new HnswAlgorithmConfiguration("hnsw-config") }
        };

        // Configure semantic search
        index.SemanticSearch = new SemanticSearch
        {
            Configurations =
            {
                new SemanticConfiguration("pinball-semantic-config", new SemanticPrioritizedFields
                {
                    TitleField = new SemanticField("sectionPath"),
                    ContentFields = { new SemanticField("content") },
                    KeywordsFields =
                    {
                        new SemanticField("gameTitle"),
                        new SemanticField("documentType"),
                        new SemanticField("manufacturer")
                    }
                })
            }
        };

        // Configure suggester
        index.Suggesters.Add(new SearchSuggester("game-suggest", "gameTitle", "manufacturer"));

        return index;
    }
}
