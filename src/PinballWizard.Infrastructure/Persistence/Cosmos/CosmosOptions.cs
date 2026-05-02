using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Strongly-typed configuration for the Cosmos DB client. Bound from
/// <c>appsettings.json</c> section <c>"Cosmos"</c> via
/// <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>.
/// </summary>
public sealed class CosmosOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cosmos";

    /// <summary>
    /// Cosmos account endpoint URL — sourced from the Bicep output
    /// <c>cosmosAccountEndpoint</c>. Required.
    /// </summary>
    [Required]
    [Url]
    public required string AccountEndpoint { get; init; }

    /// <summary>
    /// Database name within the account. Defaults to <c>pinwiz</c>;
    /// per-environment naming should distinguish dev / prod when needed.
    /// </summary>
    [Required]
    public string DatabaseName { get; init; } = "pinwiz";

    /// <summary>
    /// Container name → partition key path mapping. The bootstrapper
    /// uses this list to ensure each container exists with the correct
    /// partition key on application startup.
    /// </summary>
    public IReadOnlyList<CosmosContainerOptions> Containers { get; init; } = [];

    /// <summary>
    /// Optional override for the application name reported on the
    /// connection. Helpful in Cosmos diagnostics for distinguishing the
    /// scraper job from the API.
    /// </summary>
    public string? ApplicationName { get; init; }

    /// <summary>
    /// Preferred region(s) for client read routing. Defaults to East US
    /// 2 to match the deployment region.
    /// </summary>
    public IReadOnlyList<string> PreferredRegions { get; init; } = ["East US 2"];
}

/// <summary>
/// Per-container declaration. The bootstrapper creates the container
/// if absent on startup; if present, the partition-key path is
/// asserted (mismatch is a fatal startup error since the data layout
/// has drifted from the configuration).
/// </summary>
public sealed class CosmosContainerOptions
{
    /// <summary>Container name (e.g., <c>machines</c>, <c>ingestion_sources</c>).</summary>
    [Required]
    public required string Name { get; init; }

    /// <summary>Partition key path including the leading slash (e.g., <c>/manufacturer</c>, <c>/userId</c>).</summary>
    [Required]
    [RegularExpression("^/[a-zA-Z0-9_]+$", ErrorMessage = "Partition key path must start with '/' and contain only alphanumeric characters and underscores.")]
    public required string PartitionKeyPath { get; init; }

    /// <summary>
    /// Default time-to-live in seconds for documents in this container.
    /// Null disables TTL. -1 enables TTL but with no default expiration
    /// (per-document TTL only).
    /// </summary>
    public int? DefaultTtlSeconds { get; init; }
}
