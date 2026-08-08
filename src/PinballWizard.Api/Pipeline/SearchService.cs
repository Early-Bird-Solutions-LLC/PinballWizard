using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Pipeline;

public interface ISearchService
{
    Task<List<ScoredChunk>> SearchAsync(PreprocessedQuery query, CancellationToken ct = default);
}

public sealed record ScoredChunk
{
    public required SearchChunk Chunk { get; init; }
    public required double Score { get; init; }
}

public sealed class SearchService(
    SearchClient searchClient,
    IEmbeddingService embeddingService) : ISearchService
{
    public async Task<List<ScoredChunk>> SearchAsync(PreprocessedQuery query, CancellationToken ct = default)
    {
        var embedding = await embeddingService.GetEmbeddingAsync(query.ExpandedQuery, ct);

        var searchOptions = new SearchOptions
        {
            Size = 10,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "default",
                QueryCaption = new QueryCaption(QueryCaptionType.Extractive),
                QueryAnswer = new QueryAnswer(QueryAnswerType.Extractive)
            },
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(embedding)
                    {
                        KNearestNeighborsCount = 50,
                        Fields = { "contentVector" }
                    }
                }
            },
            Select =
            {
                "chunkId", "content", "parentDocId", "gameSlug", "gameTitle",
                "manufacturer", "documentType", "sourceType", "sourceUrl",
                "sourceName", "sectionPath", "pageNumber", "contentCategories",
                "lastUpdated"
            }
        };

        // Apply game slug filter
        var filters = new List<string>();
        if (query.GameSlugs.Count > 0)
        {
            var slugFilters = query.GameSlugs.Select(s => $"gameSlug eq '{s}'");
            filters.Add($"({string.Join(" or ", slugFilters)})");
        }

        // Apply document type filter
        if (query.Filters.Count > 0)
        {
            var typeFilters = query.Filters.Select(f => $"documentType eq '{f}'");
            filters.Add($"({string.Join(" or ", typeFilters)})");
        }

        if (filters.Count > 0)
            searchOptions.Filter = string.Join(" and ", filters);

        var response = await searchClient.SearchAsync<SearchChunk>(query.OriginalQuery, searchOptions, ct);

        var results = new List<ScoredChunk>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            if (result.Document is null) continue;
            results.Add(new ScoredChunk
            {
                Chunk = result.Document,
                Score = result.Score ?? 0
            });
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(10)
            .ToList();
    }
}
