using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Idempotent ensure-created step for the Cosmos database and its
/// containers. Safe to call on every application startup.
/// </summary>
/// <remarks>
/// The bootstrapper mirrors the container declarations in
/// <see cref="CosmosOptions.Containers"/>. If a container exists with a
/// different partition-key path than configured, the bootstrapper
/// throws — the deploying operator must reconcile the drift before the
/// app can run.
/// </remarks>
public sealed class CosmosBootstrapper
{
    private readonly CosmosClient _client;
    private readonly CosmosOptions _options;
    private readonly ILogger<CosmosBootstrapper> _logger;

    /// <summary>Initializes a new bootstrapper.</summary>
    public CosmosBootstrapper(
        CosmosClient client,
        IOptions<CosmosOptions> options,
        ILogger<CosmosBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the configured database and every configured container
    /// exists with the correct partition-key path. Throws if a container
    /// exists with a different partition-key path than configured.
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ensuring Cosmos database '{Database}' exists at {Endpoint}.", _options.DatabaseName, _options.AccountEndpoint);

        var databaseResponse = await _client.CreateDatabaseIfNotExistsAsync(
            _options.DatabaseName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var database = databaseResponse.Database;

        foreach (var containerOptions in _options.Containers)
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
            "Container '{Container}' ready (partition key {PartitionKeyPath}, default TTL {Ttl}).",
            containerOptions.Name,
            containerOptions.PartitionKeyPath,
            containerOptions.DefaultTtlSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
    }
}
