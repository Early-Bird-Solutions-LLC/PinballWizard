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
        if (containerOptions.IndexingPolicy is { } indexingPolicy)
        {
            ApplyIndexingPolicy(properties.IndexingPolicy, indexingPolicy);
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

        // Indexing-policy drift is logged (not thrown) per ADR-0025 § 3.
        // Re-applying a policy is a metadata-only operation and Cosmos
        // re-indexes existing documents in the background; this differs
        // from partition-key drift which would silently misroute writes.
        // We only LogWarning here so the operator is alerted; an
        // explicit reconcile path (re-running create-or-update) lives
        // outside the scope of `--ensure-cosmos-containers`.
        if (containerOptions.IndexingPolicy is { } expectedPolicy
            && !IndexingPolicyMatches(response.Resource.IndexingPolicy, expectedPolicy))
        {
            _logger.LogWarning(
                "Container '{Container}' indexing policy differs from configuration. Re-apply by deleting and recreating the container, or via Data Explorer. (data-plane provisioner does not auto-replace existing policies.)",
                containerOptions.Name);
        }

        // TTL drift is logged (not thrown) for the same reason as
        // indexing-policy drift: re-applying a TTL is a metadata-only
        // operation. Mismatches surface to operators so a container
        // created before the TTL decision was added (e.g.,
        // `rag_dead_letters` provisioned pre-PR-6) can be reconciled
        // by recreate or by editing the container settings via Data
        // Explorer. Null-vs-set is a real drift; null-vs-null and
        // matching-int values are silent matches.
        if (!TtlMatches(response.Resource.DefaultTimeToLive, containerOptions.DefaultTtlSeconds))
        {
            _logger.LogWarning(
                "Container '{Container}' default TTL ({ActualTtl}) differs from configured value ({ExpectedTtl}). Re-apply by recreating the container, or by editing default TTL via Data Explorer.",
                containerOptions.Name,
                FormatTtl(response.Resource.DefaultTimeToLive),
                FormatTtl(containerOptions.DefaultTtlSeconds));
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

    private static bool TtlMatches(int? actual, int? expected) =>
        actual == expected;

    private static string FormatTtl(int? ttl) =>
        ttl?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";

    private static bool IndexingPolicyMatches(IndexingPolicy actual, CosmosIndexingPolicyOptions expected)
    {
        var actualIncluded = actual.IncludedPaths.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var expectedIncluded = expected.IncludedPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var actualExcluded = actual.ExcludedPaths.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var expectedExcluded = expected.ExcludedPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return actualIncluded.SequenceEqual(expectedIncluded, StringComparer.Ordinal)
            && actualExcluded.SequenceEqual(expectedExcluded, StringComparer.Ordinal);
    }
}
