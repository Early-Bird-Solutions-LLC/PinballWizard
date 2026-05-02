using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Cosmos-backed <see cref="IIngestionSourceRepository"/>.
/// </summary>
public sealed class IngestionSourceRepository : CosmosRepository<IngestionSource>, IIngestionSourceRepository
{
    private const string ConfigPartition = "config";

    /// <summary>Initializes a new repository wrapping the <c>ingestion_sources</c> container.</summary>
    public IngestionSourceRepository(Container container, ILogger<IngestionSourceRepository> logger)
        : base(container, logger)
    {
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IngestionSource> StreamAllAsync(CancellationToken cancellationToken) =>
        StreamAsync(
            "SELECT * FROM c",
            parameters: null,
            partitionKey: ConfigPartition,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<IngestionSource> StreamEnabledAsync(CancellationToken cancellationToken) =>
        StreamAsync(
            "SELECT * FROM c WHERE c.enabled = true",
            parameters: null,
            partitionKey: ConfigPartition,
            cancellationToken: cancellationToken);
}
