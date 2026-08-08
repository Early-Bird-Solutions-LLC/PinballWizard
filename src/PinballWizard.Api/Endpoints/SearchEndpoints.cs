using PinballWizard.Api.Pipeline;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Endpoints;

public static class SearchEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/search", HandleSearch)
            .RequireRateLimiting("search");
    }

    private static async Task<IResult> HandleSearch(
        string query,
        string? gameFilter,
        string? documentTypeFilter,
        int? top,
        IQueryPreprocessor preprocessor,
        ISearchService searchService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { error = "Query is required" });

        var preprocessed = preprocessor.Process(query, gameFilter);
        var results = await searchService.SearchAsync(preprocessed, ct);

        var searchResults = results
            .Take(top ?? 10)
            .Select(r => new SearchResult
            {
                ChunkId = r.Chunk.ChunkId,
                Content = r.Chunk.Content,
                Score = r.Score,
                GameTitle = r.Chunk.GameTitle,
                DocumentType = r.Chunk.DocumentType.ToString(),
                SourceUrl = r.Chunk.SourceUrl,
                SectionPath = r.Chunk.SectionPath
            })
            .ToList();

        return Results.Ok(searchResults);
    }
}
