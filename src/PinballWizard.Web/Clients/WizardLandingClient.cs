using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PinballWizard.Application.Landing;

namespace PinballWizard.Web.Clients;

// HttpClient-based implementation of IWizardLandingClient.
//
// Sends GET /api/wizard/landing to PinballWizard.Api and deserialises the
// LandingResponse JSON. Uses the same "pinwiz-api" Aspire service-discovery
// base address as WizardStreamingClient (PR-F2 sibling).
//
// Null-safe: returns null on any non-200 status OR on transport failure
// (HttpRequestException). The Index page renders a compiled-in fallback in
// that case so the prospect's first impression never shows a broken state.
//
// Sibling diff vs IWizardStreamingClient / WizardStreamingClient (PR-F2):
//   - Same ctor shape: HttpClient + ILogger (ArgumentNullException.ThrowIfNull).
//   - Same try-catch-returns-null pattern via TryGetAsync helper to keep
//     iterators / callers free of catch blocks.
//   - Same static JsonSerializerOptions field (thread-safe after init).
//   - Different transport: GET + ReadFromJsonAsync vs POST + SSE stream.
//
// ADR-0026 § Landing surface.
public sealed class WizardLandingClient : IWizardLandingClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WizardLandingClient> _logger;

    // camelCase Web defaults without null suppression — mirrors
    // WizardLandingEndpoint.LandingJsonOptions so SystemStatus null bool?
    // fields are preserved (null ≠ false per the endpoint contract).
    private static readonly JsonSerializerOptions LandingJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };

    public WizardLandingClient(
        HttpClient httpClient,
        ILogger<WizardLandingClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<LandingResponse?> GetLandingAsync(CancellationToken cancellationToken)
    {
        // TryGetAsync isolates the try-catch from the caller so the caller
        // can perform simple null-check logic without nesting.
        var response = await TryGetAsync(cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "GET /api/wizard/landing returned {StatusCode}. Using compiled-in fallback.",
                (int)response.StatusCode);
            response.Dispose();
            return null;
        }

        try
        {
            var landing = await response.Content
                .ReadFromJsonAsync<LandingResponse>(LandingJsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return landing;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialise /api/wizard/landing response. Using compiled-in fallback.");
            return null;
        }
        finally
        {
            response.Dispose();
        }
    }

    // Isolated try-catch so GetLandingAsync stays readable.
    private async Task<HttpResponseMessage?> TryGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient
                .GetAsync("/api/wizard/landing", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Api unreachable at GET /api/wizard/landing. Using compiled-in fallback.");
            return null;
        }
    }
}
