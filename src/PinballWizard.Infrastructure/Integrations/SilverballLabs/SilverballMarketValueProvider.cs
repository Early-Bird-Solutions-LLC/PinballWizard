using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Pricing;

namespace PinballWizard.Infrastructure.Integrations.SilverballLabs;

// IMarketValueProvider implementation backed by the Silverball Labs partner API
// (ADR-0045). Lookup strategy: primary by OPDB ID for the most reliable match;
// fallback to name + manufacturer when opdbId is unavailable. Both paths are
// null-safe — pass what you have.
//
// Returns null when no pricing data is available (API 404, 429/throttle, timeout,
// or both lookups empty). Callers must never fabricate a price from a null result;
// the Wizard degrades to an honest no-live-data answer (invariant #17).
//
// Failures (distinct from "no data found") are metered on
// PinballWizardTelemetry.AiToolErrors with tag tool=getMarketValue so dashboards
// can observe pricing-tool health independently of the searchCorpus tool.
// Sealed internal — consumers wire IMarketValueProvider via AddSilverballLabsIntegration.
internal sealed class SilverballMarketValueProvider : IMarketValueProvider
{
    private const string ToolTagValue = "getMarketValue";

    private readonly ISilverballLabsClient _client;
    private readonly ILogger<SilverballMarketValueProvider> _logger;

    public SilverballMarketValueProvider(
        ISilverballLabsClient client,
        ILogger<SilverballMarketValueProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    public async Task<MarketValueResult?> GetMarketValueAsync(
        string? opdbId,
        string? machineName,
        string? manufacturer,
        CancellationToken cancellationToken = default)
    {
        SilverballPriceResponseDto? response = null;

        // Primary: OPDB ID lookup (avoids title-matching ambiguity).
        if (!string.IsNullOrWhiteSpace(opdbId))
        {
            response = await _client.GetByOpdbIdAsync(opdbId, cancellationToken).ConfigureAwait(false);
        }

        // Fallback: name + manufacturer when primary returned nothing.
        if (response is null && !string.IsNullOrWhiteSpace(machineName))
        {
            response = await _client.GetByNameAsync(machineName, manufacturer, cancellationToken).ConfigureAwait(false);
        }

        if (response is null)
        {
            // Both lookups returned null. Could be 404 ("no data for this machine"),
            // a transient error (already logged at Warning by SilverballLabsClient),
            // or no identifiers were supplied. Return null — caller degrades gracefully.
            _logger.LogDebug(
                "SilverballMarketValueProvider: no pricing data for opdbId={OpdbId} name={MachineName}.",
                opdbId,
                machineName);
            return null;
        }

        if (response.Data is null)
        {
            // API returned a response envelope but the data field is absent —
            // treat as no data. Meter as a tool error: this is unexpected from a
            // well-behaved API; investigate if the rate climbs.
            _logger.LogWarning(
                "SilverballLabs: response for opdbId={OpdbId} name={MachineName} had null data field; returning null.",
                opdbId,
                machineName);
            PinballWizardTelemetry.AiToolErrors.Add(
                1,
                new KeyValuePair<string, object?>("tool", ToolTagValue));
            return null;
        }

        return Map(response);
    }

    private static MarketValueResult Map(SilverballPriceResponseDto response)
    {
        var data = response.Data!;

        var byCondition = (data.ByCondition ?? [])
            .Select(c => new MarketValueByCondition(
                c.Condition ?? string.Empty,
                c.MedianPrice,
                c.SaleCount))
            .ToArray();

        var attribution = response.Attribution is { } attr
            ? new MarketValueAttribution(
                attr.Source ?? string.Empty,
                attr.Url ?? string.Empty,
                attr.Text ?? string.Empty)
            : new MarketValueAttribution(
                "Silverball Labs",
                string.Empty,
                "Powered by Silverball Labs · Data from PinballPrices.com");

        return new MarketValueResult(
            data.MedianPrice,
            data.AvgPrice,
            data.Min,
            data.Max,
            byCondition,
            data.TrendDirection,
            data.PriceSummary,
            data.LastSaleDate,
            attribution);
    }
}
