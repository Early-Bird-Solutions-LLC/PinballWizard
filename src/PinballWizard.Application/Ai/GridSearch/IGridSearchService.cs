namespace PinballWizard.Application.Ai.GridSearch;

/// <summary>
/// Represents a structured filter applied to a data grid.
/// </summary>
public record GridFilter(string Column, string Operator, string Value);

/// <summary>
/// The result of an AI grid search query.
/// </summary>
public record GridSearchResponse(
    IReadOnlyList<GridFilter> Filters,
    string Explanation,
    bool IsSemanticSearch = false,
    string? SemanticQuery = null);

/// <summary>
/// Service for processing natural language queries into grid filters.
/// </summary>
public interface IGridSearchService
{
    /// <summary>
    /// Parses a natural language query into a set of grid filters.
    /// </summary>
    /// <param name="query">The natural language query (e.g., "Bally machines from the 90s").</param>
    /// <param name="gridContext">Context about the grid (e.g., "admin-machines").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A response containing the filters and an explanation.</returns>
    Task<GridSearchResponse> SearchAsync(string query, string gridContext, CancellationToken cancellationToken);
}
