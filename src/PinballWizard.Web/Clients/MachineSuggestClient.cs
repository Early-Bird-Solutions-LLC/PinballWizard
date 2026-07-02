using System.Net.Http.Json;
using System.Text.Json;

namespace PinballWizard.Web.Clients;

// HttpClient-based implementation of IMachineSuggestClient.
//
// Sends GET /api/machines/suggest?q={query}&top=8 to PinballWizard.Api and
// deserialises the MachineSuggestion[] JSON.  Uses the same "pinwiz-api"
// Aspire service-discovery base address as WizardLandingClient.
//
// Fault-tolerant by design (same posture as WizardLandingClient):
//   - Returns [] on any non-200 response.
//   - Returns [] on transport failure (HttpRequestException, Polly
//     BrokenCircuitException, etc.).  Broad catch is intentional — letting
//     an infrastructure exception escape into MudAutocomplete's SearchFunc
//     would surface an unhandled exception banner, which is worse than
//     showing no suggestions.
//   - OperationCanceledException is NOT caught: cancellation is normal
//     (MudAutocomplete cancels the prior search when the user types again)
//     and the component knows how to handle it.
//   - Short queries (<2 chars) return [] without an HTTP call; the
//     MinCharacters=2 gate on the component prevents most of these, but
//     belt-and-suspenders avoids a pointless round-trip.
//
// ADR-0049 Phase 3 — landing hero typeahead.
public sealed class MachineSuggestClient : IMachineSuggestClient
{
    // top=8 is a reasonable page for a landing-hero dropdown: enough to
    // cover keyboard-navigable range without overflowing the viewport.
    private const int Top = 8;

    private readonly HttpClient _httpClient;
    private readonly ILogger<MachineSuggestClient> _logger;

    // camelCase Web defaults — the Api serialises using System.Text.Json
    // with JsonSerializerDefaults.Web (matching the landing endpoint).
    private static readonly JsonSerializerOptions SuggestJsonOptions =
        new(JsonSerializerDefaults.Web);

    public MachineSuggestClient(
        HttpClient httpClient,
        ILogger<MachineSuggestClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MachineSuggestion>> GetSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        // Guard: empty/short queries are semantically invalid; skip the call.
        if (query.Length < 2)
        {
            return [];
        }

        using var response = await TryGetAsync(query, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "GET /api/machines/suggest returned {StatusCode} for query '{Query}'. Returning no suggestions.",
                (int)response.StatusCode,
                query);
            return [];
        }

        try
        {
            var items = await response.Content
                .ReadFromJsonAsync<MachineSuggestion[]>(SuggestJsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return items ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialise /api/machines/suggest response for query '{Query}'. Returning no suggestions.",
                query);
            return [];
        }
    }

    // Isolated try-catch so GetSuggestionsAsync stays readable.
    // OperationCanceledException is NOT caught — cancellation is normal and
    // MudAutocomplete handles it by discarding the in-flight search.
    private async Task<HttpResponseMessage?> TryGetAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"/api/machines/suggest?q={Uri.EscapeDataString(query)}&top={Top}";
            return await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broad catch mirrors WizardLandingClient: Polly's BrokenCircuitException
            // (not HttpRequestException) can escape if the circuit opens, and letting
            // it propagate into MudAutocomplete's SearchFunc would surface an
            // unhandled exception panel instead of a graceful "no suggestions" state.
            _logger.LogWarning(
                ex,
                "GET /api/machines/suggest failed ({ExceptionType}) for query '{Query}'. Returning no suggestions.",
                ex.GetType().Name,
                query);
            return null;
        }
    }
}
