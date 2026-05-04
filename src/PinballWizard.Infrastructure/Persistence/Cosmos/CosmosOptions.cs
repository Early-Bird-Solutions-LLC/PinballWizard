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
    /// Full configuration key for <see cref="AccountEndpoint"/>. Exposed
    /// so callers (e.g., Aspire-vs-Managed-Identity gating logic in CLI
    /// hosts) can presence-check the key without duplicating the
    /// <c>"Cosmos:AccountEndpoint"</c> string and risking a silent
    /// drift if the section is ever renamed.
    /// </summary>
    public const string AccountEndpointKey = $"{SectionName}:{nameof(AccountEndpoint)}";

    /// <summary>
    /// Connection-string name Aspire uses when wiring a Cosmos resource
    /// via <c>builder.AddAzureCosmosDB("cosmos")</c>. CLI hosts use
    /// <c>IConfiguration.GetConnectionString(CosmosConnectionName)</c>
    /// to detect Aspire-injected connections.
    /// </summary>
    public const string CosmosConnectionName = "cosmos";

    /// <summary>
    /// Full configuration key for <see cref="AccountResourceId"/>. Exposed
    /// alongside <see cref="AccountEndpointKey"/> so CLI hosts can
    /// presence-check both without duplicating the section strings.
    /// </summary>
    public const string AccountResourceIdKey = $"{SectionName}:{nameof(AccountResourceId)}";

    /// <summary>
    /// Cosmos account endpoint URL — sourced from the Bicep output
    /// <c>cosmosAccountEndpoint</c> when running against a deployed
    /// Cosmos account. Optional when an <see cref="Microsoft.Azure.Cosmos.CosmosClient"/>
    /// is already registered in DI by an external integration (e.g.,
    /// .NET Aspire's <c>AddAzureCosmosClient("cosmos")</c>, which
    /// builds the client from the Aspire-injected connection string
    /// and supersedes this endpoint).
    /// </summary>
    [Url]
    public string? AccountEndpoint { get; init; }

    /// <summary>
    /// ARM resource ID of the Cosmos account, sourced from the Bicep
    /// output <c>cosmosAccountResourceId</c>. Required when
    /// <see cref="CosmosBootstrapper"/> runs against deployed Cosmos
    /// because schema bootstrap (database / container CRUD) goes
    /// through the ARM SDK — Cosmos's data-plane RBAC does not grant
    /// schema-mutation actions, regardless of role definition.
    /// Format:
    /// <c>/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.DocumentDB/databaseAccounts/{name}</c>.
    /// Leave null when running against the Aspire preview emulator —
    /// the emulator authenticates the SDK with the master key in the
    /// connection string, which permits data-plane schema CRUD without
    /// any ARM round-trip.
    /// </summary>
    public string? AccountResourceId { get; init; }

    /// <summary>
    /// Database name within the account. Defaults to <c>pinwiz</c>;
    /// per-environment naming should distinguish dev / prod when needed.
    /// </summary>
    [Required]
    public string DatabaseName { get; init; } = "pinwiz";

    /// <summary>
    /// Container name -> partition key path mapping. The bootstrapper
    /// uses this list to ensure each container exists with the correct
    /// partition key. Defaults match the canonical Phase 1 container
    /// names the repositories already reference
    /// (<see cref="MachineRepository"/> uses <c>machines</c> with
    /// partition key <c>/manufacturer</c> per ADR 0011;
    /// <see cref="IngestionSourceRepository"/> uses
    /// <c>ingestion_sources</c> with partition key
    /// <c>/partitionKey</c>). Configuration binding REPLACES the list
    /// (does not merge), so adding a <c>Cosmos:Containers</c> entry to
    /// configuration overrides these defaults entirely.
    /// </summary>
    public IReadOnlyList<CosmosContainerOptions> Containers { get; init; } =
    [
        new() { Name = "machines", PartitionKeyPath = "/manufacturer" },
        new() { Name = "ingestion_sources", PartitionKeyPath = "/partitionKey" },
    ];

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
