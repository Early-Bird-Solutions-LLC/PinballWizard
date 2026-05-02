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
}
