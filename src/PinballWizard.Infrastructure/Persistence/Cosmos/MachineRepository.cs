using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Cosmos-backed <see cref="IMachineRepository"/>.
/// </summary>
public sealed class MachineRepository : CosmosRepository<Machine>, IMachineRepository
{
    /// <summary>Initializes a new repository wrapping the <c>machines</c> container.</summary>
    public MachineRepository(Container container, ILogger<MachineRepository> logger)
        : base(container, logger)
    {
    }

    /// <inheritdoc />
    public Task<Machine?> GetByOpdbIdAsync(string opdbId, string manufacturer, CancellationToken cancellationToken) =>
        GetByIdAsync(opdbId, manufacturer, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Machine> StreamByManufacturerAsync(string manufacturer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        return StreamAsync(
            "SELECT * FROM c",
            parameters: null,
            partitionKey: manufacturer,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Machine> QueryByTitleAsync(string title, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // STRINGEQUALS with the third argument true performs a
        // case-insensitive comparison server-side, so "foo fighters"
        // matches a stored "Foo Fighters" without the function tool
        // having to know which casing was used by OPDB. Cross-partition
        // (partitionKey: null) — the function tool doesn't know the
        // manufacturer up front. At ~2,400 machines the RU cost of a
        // cross-partition equality match is small (single-digit RU
        // typical for sub-thousand-row scans).
        return StreamAsync(
            "SELECT * FROM c WHERE STRINGEQUALS(c.title, @title, true)",
            parameters: new Dictionary<string, object> { ["title"] = title },
            partitionKey: null,
            cancellationToken: cancellationToken);
    }
}
