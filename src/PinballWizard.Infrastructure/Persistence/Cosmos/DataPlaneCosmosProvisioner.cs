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

    internal static ContainerProperties BuildContainerProperties(CosmosContainerOptions containerOptions)
    {
        var properties = new ContainerProperties(containerOptions.Name, containerOptions.PartitionKeyPath);
        if (containerOptions.DefaultTtlSeconds is { } ttl)
        {
            properties.DefaultTimeToLive = ttl;
        }
        if (containerOptions.IndexingPolicy is { } indexingPolicy)
        {
            ApplyIndexingPolicy(properties.IndexingPolicy, indexingPolicy);
        }
        return properties;
    }

    private async Task EnsureContainerAsync(
        Database database,
        CosmosContainerOptions containerOptions,
        CancellationToken cancellationToken)
    {
        var properties = BuildContainerProperties(containerOptions);

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

        var indexDrift = containerOptions.IndexingPolicy is { } expectedPolicy
            && !IndexingPolicyMatches(response.Resource.IndexingPolicy, expectedPolicy);
        var ttlDrift = !TtlMatches(response.Resource.DefaultTimeToLive, containerOptions.DefaultTtlSeconds);

        if (indexDrift || ttlDrift)
        {
            await database.GetContainer(containerOptions.Name)
                .ReplaceContainerAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Container '{Container}' reconciled via data-plane to match configuration ({What}).",
                containerOptions.Name,
                indexDrift && ttlDrift ? "index policy + default TTL" : indexDrift ? "index policy" : "default TTL");
            return;
        }

        _logger.LogInformation(
            "Container '{Container}' ready via data-plane SDK (partition key {PartitionKeyPath}, default TTL {Ttl}, indexing {Indexing}).",
            containerOptions.Name,
            containerOptions.PartitionKeyPath,
            containerOptions.DefaultTtlSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            containerOptions.IndexingPolicy is null ? "default" : "selective");
    }

    private static void ApplyIndexingPolicy(IndexingPolicy target, CosmosIndexingPolicyOptions source)
    {
        target.IncludedPaths.Clear();
        target.ExcludedPaths.Clear();
        foreach (var path in source.IncludedPaths)
        {
            target.IncludedPaths.Add(new IncludedPath { Path = path });
        }
        foreach (var path in source.ExcludedPaths)
        {
            target.ExcludedPaths.Add(new ExcludedPath { Path = path });
        }
    }

    internal static bool TtlMatches(int? actual, int? expected) =>
        actual == expected
        || (expected is null && actual == -2); // Aspire emulator sentinel: -2 means "no default TTL"

    // Cosmos automatically injects this system-managed path into ExcludedPaths on every
    // container. It is never present in our CosmosIndexingPolicyOptions configuration,
    // so a naive set-equality check would always report drift. Strip it from the actual
    // set before comparing so the drift check remains idempotent.
    private const string CosmosSystemEtagExcludedPath = "/\"_etag\"/?";

    internal static bool IndexingPolicyMatches(IndexingPolicy actual, CosmosIndexingPolicyOptions expected)
    {
        var actualIncluded = actual.IncludedPaths.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var expectedIncluded = expected.IncludedPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var actualExcluded = actual.ExcludedPaths.Select(p => p.Path)
            .Where(p => p != CosmosSystemEtagExcludedPath)
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var expectedExcluded = expected.ExcludedPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return actualIncluded.SequenceEqual(expectedIncluded, StringComparer.Ordinal)
            && actualExcluded.SequenceEqual(expectedExcluded, StringComparer.Ordinal);
    }
}
