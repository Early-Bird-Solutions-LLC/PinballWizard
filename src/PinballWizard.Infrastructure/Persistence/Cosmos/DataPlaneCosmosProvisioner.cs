using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Provisioner for the Cosmos preview emulator path: the
/// <see cref="CosmosClient"/> is master-key-authed (the Aspire-injected
/// connection string includes <c>AccountKey=…</c>), so
/// <c>CreateDatabaseIfNotExistsAsync</c> /
/// <c>CreateContainerIfNotExistsAsync</c> on the data-plane endpoint
/// succeed without any RBAC plumbing. Behaviorally identical to the
/// pre-PR-#63 <c>CosmosBootstrapper.EnsureCreatedAsync</c> body.
/// </summary>
public sealed class DataPlaneCosmosProvisioner : ICosmosProvisioner
{
    private readonly CosmosClient _client;
    private readonly ILogger<DataPlaneCosmosProvisioner> _logger;

    /// <summary>Initializes a new data-plane provisioner.</summary>
    public DataPlaneCosmosProvisioner(
        CosmosClient client,
        ILogger<DataPlaneCosmosProvisioner> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnsureDatabaseAndContainersAsync(
        string databaseName,
        IReadOnlyList<CosmosContainerOptions> containers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(containers);

        _logger.LogInformation("Ensuring Cosmos database '{Database}' via data-plane SDK at {Endpoint}.",
            databaseName, _client.Endpoint);

        var databaseResponse = await _client.CreateDatabaseIfNotExistsAsync(
            databaseName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var database = databaseResponse.Database;

        foreach (var containerOptions in containers)
        {
            await EnsureContainerAsync(database, containerOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureContainerAsync(
        Database database,
        CosmosContainerOptions containerOptions,
        CancellationToken cancellationToken)
    {
        var properties = new ContainerProperties(containerOptions.Name, containerOptions.PartitionKeyPath);
        if (containerOptions.DefaultTtlSeconds is { } ttl)
        {
            properties.DefaultTimeToLive = ttl;
        }

        var response = await database.CreateContainerIfNotExistsAsync(
            properties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var existingPartitionKeyPath = response.Resource.PartitionKeyPath;
        if (!string.Equals(existingPartitionKeyPath, containerOptions.PartitionKeyPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Container '{containerOptions.Name}' exists with partition key path '{existingPartitionKeyPath}', " +
                $"but configuration requires '{containerOptions.PartitionKeyPath}'. Reconcile the drift before starting the app.");
        }

        _logger.LogInformation(
            "Container '{Container}' ready via data-plane SDK (partition key {PartitionKeyPath}, default TTL {Ttl}).",
            containerOptions.Name,
            containerOptions.PartitionKeyPath,
            containerOptions.DefaultTtlSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
    }
}
