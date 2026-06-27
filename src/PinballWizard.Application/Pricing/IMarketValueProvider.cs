namespace PinballWizard.Application.Pricing;

// Application-layer abstraction over the live pricing data source.
// Infrastructure implementation: SilverballMarketValueProvider (Infrastructure/
// Integrations/SilverballLabs/). DI-gated on SilverballLabsOptions.ApiKeyKey
// presence — when absent, nothing registers IMarketValueProvider and the
// getMarketValue tool is not wired (Wizard degrades gracefully).
//
// Lookup strategy (ADR-0045): primary by OPDB ID for the most reliable
// match (avoids title-matching ambiguity); fallback to name + manufacturer
// when opdbId is unavailable. Both paths are null-safe — pass what you have.
//
// Returns null when no pricing data is available for the machine (API 404,
// 429/throttle, timeout, or empty result). Callers must never fabricate a
// price from a null result; the Wizard degrades to an honest refusal.
public interface IMarketValueProvider
{
    Task<MarketValueResult?> GetMarketValueAsync(
        string? opdbId,
        string? machineName,
        string? manufacturer,
        CancellationToken cancellationToken = default);
}
