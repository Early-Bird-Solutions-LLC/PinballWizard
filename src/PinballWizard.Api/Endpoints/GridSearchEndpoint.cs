using System.Text.Json;
using PinballWizard.Application.Ai.GridSearch;

namespace PinballWizard.Api.Endpoints;

// GET /api/search/grid?q={query}&context={context} — AI-driven grid search parsing.
public static class GridSearchEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
