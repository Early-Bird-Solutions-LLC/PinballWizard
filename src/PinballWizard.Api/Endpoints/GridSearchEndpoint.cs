using System.Text.Json;
using PinballWizard.Application.Ai.GridSearch;

namespace PinballWizard.Api.Endpoints;

// GET /api/search/grid?q={query}&context={context} — AI-driven grid search parsing.
public static class GridSearchEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Mirrors the "### " section headers in Ai/Agents/GridSearch.md — the prompt's per-grid
    // column schemas. Keeping this list in sync is intentionally a review-time concern (a
    // contract test analogous to SourceAliasContractTests would automate it), not because it's
    // exploitable: an unrecognized context still can't reach anything beyond a filter-tuple
    // parse. Rejecting it here removes the free-text prompt-injection surface at the boundary.
    private static readonly HashSet<string> ValidGridContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin-machines",
        "admin-jobs",
        "admin-document-triage",
        "admin-manufacturers",
        "admin-sources",
        "admin-job-detail",
        "admin-link-overrides",
        "admin-document-list",
        "public-document-list",
    };

    public static IEndpointRouteBuilder MapGridSearchEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/search/grid", HandleAsync)
            .WithName("GridSearch")
            .WithDisplayName("Grid Search")
            .WithDescription("Parses a natural language query into grid filters using AI.")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var q = context.Request.Query["q"].ToString();
        var gridContext = context.Request.Query["context"].ToString();

        if (string.IsNullOrWhiteSpace(q))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!ValidGridContexts.Contains(gridContext))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var searchService = services.GetRequiredService<IGridSearchService>();

        var result = await searchService
            .SearchAsync(q, gridContext, cancellationToken)
            .ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        var json = JsonSerializer.Serialize(result, JsonOptions);
        await context.Response.WriteAsync(json, cancellationToken).ConfigureAwait(false);
    }
}
