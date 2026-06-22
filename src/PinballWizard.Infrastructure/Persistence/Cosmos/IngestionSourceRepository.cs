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

    private readonly ILogger<IngestionSourceRepository> _logger;

    /// <summary>Initializes a new repository wrapping the <c>ingestion_sources</c> container.</summary>
    public IngestionSourceRepository(Container container, ILogger<IngestionSourceRepository> logger)
        : base(container, logger)
    {
        _logger = logger;
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

    /// <inheritdoc />
    public async Task RecordRunResultAsync(
        string sourceId,
        IngestionSourceRunResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(result);

        var existing = await GetByIdAsync(sourceId, ConfigPartition, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            _logger.LogWarning(
                "RecordRunResultAsync: ingestion source '{SourceId}' not found in Cosmos. " +
                "The seeder may not have run; skipping the write-back rather than failing the sync. " +
                "Re-run --seed-ingestion-sources to populate the missing entry.",
                sourceId);
            return;
        }

        existing.LastRunAt = result.RunAt;
        if (result.Succeeded)
        {
            existing.LastSuccessAt = result.RunAt;
        }
        else
        {
            existing.TotalRunFailures++;
        }
        existing.TotalDocumentsDiscovered += result.DocumentsDiscovered;

        await UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var existing = await GetByIdAsync(id, ConfigPartition, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            _logger.LogWarning(
                "SetEnabledAsync: ingestion source '{SourceId}' not found in Cosmos. " +
                "Skipping the write-back rather than fabricating success.",
                id);
            return false;
        }

        existing.Enabled = enabled;
        await UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
