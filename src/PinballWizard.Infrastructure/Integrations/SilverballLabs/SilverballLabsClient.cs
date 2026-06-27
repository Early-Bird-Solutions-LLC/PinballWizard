using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Integrations.SilverballLabs;

// Seam for testing — allows SilverballMarketValueProvider to be tested without a
// real HttpClient. Made public so NSubstitute/Castle DynamicProxy can mock it in
// the test assembly without requiring InternalsVisibleTo matching exact Castle keys.
public interface ISilverballLabsClient
{
    Task<SilverballPriceResponseDto?> GetByOpdbIdAsync(string opdbId, CancellationToken ct);
    Task<SilverballPriceResponseDto?> GetByNameAsync(string gameName, string? manufacturer, CancellationToken ct);
}

// Typed HTTP client for the Silverball Labs live-pricing API (ADR-0045).
//
// Not a scraper — does NOT route through the politeness gate. This is
// authenticated use of Silverball Labs' partner API under an explicit API key
// (ADR-0045). The HttpClient is configured with the API key in
// DefaultRequestHeaders via AddSilverballLabsIntegration; the timeout is also
// set on the client.
//
// Lookup strategy (ADR-0045): primary by OPDB ID for the most reliable match
// (avoids title-matching ambiguity); fallback to name + manufacturer when
// opdbId is unavailable. Both methods fail closed — any non-success status
// other than the explicitly handled ones (404, 429, 5xx) is caught and logged.
// The public methods never throw; callers degrade gracefully to a no-pricing
// answer.
// Typed HTTP client, sealed. Consumers wire IMarketValueProvider via
// AddSilverballLabsIntegration; external code should prefer ISilverballLabsClient
// or IMarketValueProvider, not this concrete type.
public sealed class SilverballLabsClient : ISilverballLabsClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SilverballLabsClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public SilverballLabsClient(HttpClient http, ILogger<SilverballLabsClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _logger = logger;
    }

    // Fetch live market-value data by OPDB ID.
    // GET /prices/{opdbId} (relative to the BaseAddress set on the HttpClient).
    // Returns null on 404, 429, 5xx, timeout, or any other exception.
    public async Task<SilverballPriceResponseDto?> GetByOpdbIdAsync(string opdbId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);

        var url = $"prices/{Uri.EscapeDataString(opdbId)}";
        return await SendAsync(url, ct).ConfigureAwait(false);
    }

    // Fetch live market-value data by game name and optional manufacturer.
    // GET /prices?gameName={gameName}&manufacturer={manufacturer}
    // Returns null on 404, 429, 5xx, timeout, or any other exception.
    public async Task<SilverballPriceResponseDto?> GetByNameAsync(
        string gameName,
        string? manufacturer,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);

        var url = string.IsNullOrWhiteSpace(manufacturer)
            ? $"prices?gameName={Uri.EscapeDataString(gameName)}"
            : $"prices?gameName={Uri.EscapeDataString(gameName)}&manufacturer={Uri.EscapeDataString(manufacturer)}";

        return await SendAsync(url, ct).ConfigureAwait(false);
    }

    // Shared send + degrade path. Invariant #17: degrade visibly, never fabricate.
    // 404 → null (debug log; expected for unknown machines).
    // 429 → null (warning; we are being rate-limited).
    // 5xx → null (warning with status code; Silverball infra issue).
    // Timeout / generic exception → null (warning with exception).
    private async Task<SilverballPriceResponseDto?> SendAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "SilverballLabs: no pricing data for request {Url} (404).",
                    relativeUrl);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "SilverballLabs: rate-limited (429) for request {Url}; returning null.",
                    relativeUrl);
                return null;
            }

            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "SilverballLabs: server error {StatusCode} for request {Url}; returning null.",
                    (int)response.StatusCode,
                    relativeUrl);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SilverballPriceResponseDto>(json, JsonOptions);
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout fires as a TaskCanceledException (subclass of
            // OperationCanceledException) with ct.IsCancellationRequested=false and
            // InnerException=TimeoutException. Real caller-cancellation has
            // ct.IsCancellationRequested=true. Re-throw only for real cancellation;
            // treat timeout as a degrade-and-return-null case (invariant #17).
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            _logger.LogWarning(
                ex,
                "SilverballLabs: request to {Url} timed out; returning null.",
                relativeUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SilverballLabs: unexpected error for request {Url}; returning null.",
                relativeUrl);
            return null;
        }
    }
}
