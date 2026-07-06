using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Hosting;
using PinballWizard.Application.Observability;

namespace PinballWizard.Application.Ai.GridSearch;

public sealed class GridSearchService : IGridSearchService
{
    private readonly IFoundryAgentFactory _agentFactory;
    private readonly ILogger<GridSearchService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GridSearchService(
        IFoundryAgentFactory agentFactory,
        ILogger<GridSearchService> logger)
    {
        _agentFactory = agentFactory;
        _logger = logger;
    }

    public async Task<GridSearchResponse> SearchAsync(string query, string gridContext, CancellationToken cancellationToken)
    {
        var agent = _agentFactory.GetAgent(AgentName.GridSearch);
        
        var prompt = $"Query: \"{query}\"\nGrid: \"{gridContext}\"";
        
        try
        {
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            var text = response.Text;

            // Extract JSON from response (sometimes models wrap it in markdown blocks)
            var json = ExtractJson(text);

            if (string.IsNullOrWhiteSpace(json))
            {
                // Agent output is seeded by the caller's free-text query — sanitize like the query itself (CWE-117).
                _logger.LogWarning("Agent returned non-JSON response for grid search: {Response}", LogSanitizer.ForLog(text));
                return new GridSearchResponse([], "I couldn't parse your query into filters. Try a different phrasing.", false, null);
            }

            var result = JsonSerializer.Deserialize<GridSearchResponse>(json, JsonOptions);
            return result ?? new GridSearchResponse([], "Failed to parse search results.", false, null);
        }
        catch (Exception ex)
        {
            // query is free text from an AllowAnonymous endpoint — sanitize before logging (CWE-117).
            _logger.LogError(ex, "Error during AI grid search for query '{Query}' in context '{Context}'", LogSanitizer.ForLog(query), gridContext);
            return new GridSearchResponse([], "An error occurred while processing your search.", false, null);
        }
    }

    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            return text.Substring(start, end - start + 1);
        }

        return string.Empty;
    }
}
