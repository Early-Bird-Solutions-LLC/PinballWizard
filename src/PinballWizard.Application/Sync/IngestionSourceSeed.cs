using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Sync;

public sealed class IngestionSourceSeed
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("scraperImplKey")]
    public required string ScraperImplKey { get; init; }

    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }

    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    [JsonPropertyName("cadence")]
    public required string Cadence { get; init; }

    [JsonPropertyName("politenessOverrides")]
    public PolitenessOverrides? PolitenessOverrides { get; init; }

    [JsonPropertyName("sourceGroup")]
    public required string SourceGroup { get; init; }

    [JsonPropertyName("discoveryStatus")]
    public string? DiscoveryStatus { get; init; }

    [JsonPropertyName("discoveryNotes")]
    public string? DiscoveryNotes { get; init; }

    [JsonPropertyName("discoveryDate")]
    public DateOnly? DiscoveryDate { get; init; }
}
