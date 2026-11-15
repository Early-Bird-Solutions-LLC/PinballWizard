using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the IFPA WPPR live-player integration (ADR-0067).
// Partner API key is sourced from Key Vault secret `Ifpa-ApiKey`
// (surfaced as Ifpa__ApiKey in ACA) or the machine env var IFPA__APIKEY /
// Ifpa:ApiKey in local dev. Never commit the key.
//
// IFPA OpenAPI 2.1 authenticates with an `api_key` **query** parameter
// (not Bearer). The client appends that query on every request.
//
// DI registration is gated on ApiKeyKey presence — absent config means
// IIfpaPlayerProvider is not registered and the IFPA Wizard tools degrade
// gracefully (return null). No fallback to MatchPlay/Opdb tokens: IFPA
// issues a distinct key from ifpapinball.com/api.
public sealed class IfpaOptions
{
    public const string SectionName = "Ifpa";

    // Full key for the partner API key — used by DI-gating logic.
    public const string ApiKeyKey = $"{SectionName}:ApiKey";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.ifpapinball.com/";

    // Partner key sent as ?api_key=… on IFPA API calls. Empty = not registered
    // (DI gate checks ApiKeyKey presence before binding).
    public string ApiKey { get; set; } = string.Empty;

    // Per-request HTTP timeout. Player search + profile are small JSON.
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 30;
}
