using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Landing;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Infrastructure.Landing;

// Infrastructure implementation of ICosmosCanaryProbe. Mirrors the probe
// technique used by CosmosHealthCheck: a lightweight ReadContainerAsync
// call against the "machines" container verifies data-plane auth,
// network path, and partition-routing without paying the cost of a full
// document read (~1 RU per probe).
//
// Registered as a singleton by the Cosmos DI extension when the
// CosmosClient is wired (see ServiceCollectionExtensions.AddCosmosPersistence).
// Optional in SystemStatusProvider — absent when Cosmos is not configured.
public sealed class CosmosCanaryProbe : ICosmosCanaryProbe
{
    private readonly CosmosClient _client;
    private readonly string _databaseName;
    private readonly ILogger<CosmosCanaryProbe> _logger;

    public CosmosCanaryProbe(
        CosmosClient client,
        IOptions<CosmosOptions> options,
        ILogger<CosmosCanaryProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _databaseName = options.Value.DatabaseName;
        _logger = logger;
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var container = _client.GetContainer(_databaseName, CosmosHealthCheck.CanaryContainerName);
            await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException ex)
        {
            _logger.LogDebug(
                "CosmosCanaryProbe: ReadContainerAsync returned CosmosException " +
                "(status={StatusCode}, subStatus={SubStatusCode}): {Message}",
                (int)ex.StatusCode, ex.SubStatusCode, ex.Message);
            return false;
        }
    }
}
