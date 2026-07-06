using System.Net.Http.Json;
using System.Text.Json;
using PinballWizard.Application.Ai.GridSearch;

namespace PinballWizard.Web.Clients;

public interface IGridSearchClient
{
    Task<GridSearchResponse> SearchAsync(string query, string gridContext, CancellationToken cancellationToken);
}

public sealed class GridSearchClient : IGridSearchClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GridSearchClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GridSearchClient(
        HttpClient httpClient,
        ILogger<GridSearchClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<GridSearchResponse> SearchAsync(string query, string gridContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new GridSearchResponse([], string.Empty);
        }

        try
        {
            var url = $"/api/search/grid?q={Uri.EscapeDataString(query)}&context={Uri.EscapeDataString(gridContext)}";
            var response = await _httpClient.GetFromJsonAsync<GridSearchResponse>(url, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return response ?? new GridSearchResponse([], "Received empty response from search service.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to call grid search API for query '{Query}'", query);
            return new GridSearchResponse([], "Failed to connect to search service.");
        }
    }
}
