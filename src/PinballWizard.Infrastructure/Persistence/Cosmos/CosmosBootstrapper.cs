using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Idempotent ensure-created step for the Cosmos database and its
/// containers. Safe to call on every application startup, and from the
/// CLI's <c>--ensure-cosmos-containers</c> post-deploy smoke-test.
/// </summary>
/// <remarks>
/// Delegates to an <see cref="ICosmosProvisioner"/>, which is one of:
/// <list type="bullet">
///   <item>
///     <see cref="DataPlaneCosmosProvisioner"/> — used when the
///     Cosmos client is master-key-authed (Aspire preview emulator).
///     Issues schema CRUD via the data-plane SDK.
///   </item>
///   <item>
///     <see cref="ArmCosmosProvisioner"/> — used when the Cosmos
///     client is AAD-authed (deployed Cosmos via Managed Identity).
///     Issues schema CRUD via Azure Resource Manager because Cosmos's
///     data-plane RBAC genuinely does NOT model schema mutations
///     (PR #62 attempted to grant <c>sqlDatabases/*</c> via a custom
///     data-plane role and Azure rejected the deploy as "not a valid
///     SQL data action").
///   </item>
/// </list>
/// The selection happens in <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>:
/// when <see cref="CosmosOptions.AccountResourceId"/> is set, the ARM
/// provisioner is registered; otherwise the data-plane provisioner
/// covers the local-emulator scenario.
/// </remarks>
public sealed class CosmosBootstrapper
{
    private readonly ICosmosProvisioner _provisioner;
    private readonly CosmosOptions _options;
    private readonly ILogger<CosmosBootstrapper> _logger;

    /// <summary>Initializes a new bootstrapper.</summary>
    public CosmosBootstrapper(
        ICosmosProvisioner provisioner,
        IOptions<CosmosOptions> options,
        ILogger<CosmosBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _provisioner = provisioner;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the configured database and every configured container
    /// exists with the correct partition-key path. Throws if a
    /// container exists with a different partition-key path than
    /// configured (drift is fatal — operator must reconcile).
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Bootstrapping Cosmos database '{Database}' with {ContainerCount} container(s) via {Provisioner}.",
            _options.DatabaseName,
            _options.Containers.Count,
            _provisioner.GetType().Name);

        await _provisioner.EnsureDatabaseAndContainersAsync(
            _options.DatabaseName,
            _options.Containers,
            cancellationToken).ConfigureAwait(false);
    }
}
