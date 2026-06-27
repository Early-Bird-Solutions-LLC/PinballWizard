using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Silverball Labs live-pricing integration (ADR-0045).
// Partner API key is sourced from Key Vault secret `silverball-api-key`
// (surfaced as SilverballLabs__ApiKey in ACA) or the machine env var
// SILVERBALL_API_KEY in local dev. Never commit the key.
// DI registration is gated on ApiKeyKey presence — absent config means
// IMarketValueProvider is not registered and the getMarketValue tool is
// not wired, so the Wizard degrades gracefully to a no-pricing answer.
public sealed class SilverballLabsOptions
{
    public const string SectionName = "SilverballLabs";

    // Full key for the partner API key — used by DI-gating logic
    // to presence-check before registering the integration, the same
    // pattern as OpdbOptions.BaseUrlKey and KineticistOptions.ApiKeyKey.
    public const string ApiKeyKey = $"{SectionName}:ApiKey";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://silverballlabs.com/api/v1";

    // Partner key sent via X-API-Key header. Empty = not registered
    // (DI gate checks ApiKeyKey presence before binding).
    public string ApiKey { get; set; } = string.Empty;

    // Per-request HTTP timeout. Silverball caches results for 1 hour on
    // their end; a 30 s budget is generous for a cached REST response.
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 30;
}
