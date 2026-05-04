using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.CosmosDB;
using Azure.ResourceManager.CosmosDB.Models;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Provisioner for deployed Cosmos: drives database and container CRUD
/// through Azure Resource Manager (the management plane). Required for
/// AAD-authed clients because Cosmos's data-plane RBAC genuinely does
/// not model schema-mutation actions; PR #62 attempted to add
/// <c>sqlDatabases/*</c> to a custom data-plane role and Azure
/// rejected the deploy with "not a valid SQL data action."
/// </summary>
/// <remarks>
/// Authorization is by Azure RBAC (ARM) at the
/// <c>Microsoft.DocumentDB/databaseAccounts</c> scope. The developer
/// principal in dev environments inherits this from subscription
/// Owner; the production runtime principal needs <c>Cosmos DB
/// Operator</c> (<c>230815da-be43-4aae-9cb4-875f7bd000aa</c>) at the
/// account scope or higher.
/// <para>
/// The provisioner intentionally requires a fully-qualified ARM
/// <see cref="ResourceIdentifier"/> for the Cosmos account — the
/// alternative (subscription/resource-group walk to discover the
/// account by name) would silently mask environmental misconfigs and
/// add startup latency. The resource ID is sourced from the Bicep
/// output <c>cosmosAccountResourceId</c>; CLI hosts read it from
/// <see cref="CosmosOptions.AccountResourceId"/>.
/// </para>
/// </remarks>
public sealed class ArmCosmosProvisioner : ICosmosProvisioner
{
    private readonly ArmClient _armClient;
    private readonly ResourceIdentifier _accountResourceId;
    private readonly ILogger<ArmCosmosProvisioner> _logger;

    /// <summary>Initializes a new ARM provisioner.</summary>
    public ArmCosmosProvisioner(
        ArmClient armClient,
        ResourceIdentifier accountResourceId,
        ILogger<ArmCosmosProvisioner> logger)
    {
        ArgumentNullException.ThrowIfNull(armClient);
        ArgumentNullException.ThrowIfNull(accountResourceId);
        ArgumentNullException.ThrowIfNull(logger);
        _armClient = armClient;
        _accountResourceId = accountResourceId;
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

        // Log only the account name, not the full ARM resource ID (which
        // includes the subscription ID + resource group). The subscription ID
        // is a public identifier per ADR 0010, but log telemetry is shared
        // more broadly than this repo (App Insights / Log Analytics) and
        // CodeQL flags ResourceId-substring values as a sensitive-data
        // smell. The account name is sufficient for the operator-facing
        // diagnostic.
        _logger.LogInformation("Ensuring Cosmos database '{Database}' via ARM on account '{AccountName}'.",
            databaseName, _accountResourceId.Name);

        var account = _armClient.GetCosmosDBAccountResource(_accountResourceId);
        var databases = account.GetCosmosDBSqlDatabases();

        // Probe-then-create. The CreateOrUpdateAsync ARM call is idempotent
        // for the database resource itself, but it would silently overwrite
        // any database-level settings (autoscale throughput, conflict
        // resolution policy, etc.) that an operator made out-of-band. Today
        // we don't configure any database-level settings, but the probe
        // path mirrors the container path's symmetry and surfaces existence
        // explicitly in the log so an operator reading `--ensure-cosmos-containers`
        // output knows whether the database was created or already present.
        CosmosDBSqlDatabaseResource database;
        if (await databases.ExistsAsync(databaseName, cancellationToken).ConfigureAwait(false))
        {
            var existing = await databases.GetAsync(databaseName, cancellationToken).ConfigureAwait(false);
            database = existing.Value;
            _logger.LogInformation("Database '{Database}' already present via ARM.", databaseName);
        }
        else
        {
            // CosmosDBSqlDatabaseCreateOrUpdateContent's ctor requires a Location;
            // for child resources of a Cosmos account, ARM uses the parent
            // account's region regardless of what we pass. We use a deterministic
            // placeholder (the project's documented dev region per
            // CosmosOptions.PreferredRegions and infra/main-shared.bicep's
            // `location` parameter) so the request is well-formed.
            var databaseContent = new CosmosDBSqlDatabaseCreateOrUpdateContent(
                AzureLocation.EastUS2,
                new CosmosDBSqlDatabaseResourceInfo(databaseName));

            var databaseResponse = await databases.CreateOrUpdateAsync(
                WaitUntil.Completed,
                databaseName,
                databaseContent,
                cancellationToken).ConfigureAwait(false);
            database = databaseResponse.Value;
            _logger.LogInformation("Database '{Database}' created via ARM.", databaseName);
        }

        foreach (var containerOptions in containers)
        {
            await EnsureContainerAsync(database, containerOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureContainerAsync(
        CosmosDBSqlDatabaseResource database,
        CosmosContainerOptions containerOptions,
        CancellationToken cancellationToken)
    {
        var containers = database.GetCosmosDBSqlContainers();

        // Probe-then-create so we can detect partition-key drift on
        // existing containers BEFORE issuing a create-or-update that
        // would either silently no-op (matching path) or fail with a
        // generic ARM error (mismatching path). The explicit check
        // produces the same load-bearing diagnostic the data-plane
        // path emits.
        CosmosDBSqlContainerResource? existing = null;
        try
        {
            existing = await containers.GetAsync(containerOptions.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Container does not exist yet — fall through to create.
        }

        if (existing is not null)
        {
            var existingPaths = existing.Data.Resource?.PartitionKey?.Paths;
            var existingPath = existingPaths is { Count: > 0 } ? existingPaths[0] : null;
            if (!string.Equals(existingPath, containerOptions.PartitionKeyPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Container '{containerOptions.Name}' exists with partition key path '{existingPath}', " +
                    $"but configuration requires '{containerOptions.PartitionKeyPath}'. Reconcile the drift before starting the app.");
            }

            _logger.LogInformation(
                "Container '{Container}' already present via ARM (partition key {PartitionKeyPath}).",
                containerOptions.Name,
                containerOptions.PartitionKeyPath);
            return;
        }

        var resource = new CosmosDBSqlContainerResourceInfo(containerOptions.Name)
        {
            PartitionKey = new CosmosDBContainerPartitionKey
            {
                Paths = { containerOptions.PartitionKeyPath },
                Kind = CosmosDBPartitionKind.Hash,
            },
        };
        if (containerOptions.DefaultTtlSeconds is { } ttl)
        {
            resource.DefaultTtl = ttl;
        }

        var content = new CosmosDBSqlContainerCreateOrUpdateContent(
            // Same placeholder rationale as the database create above —
            // ARM uses the parent account's region for child resources.
            AzureLocation.EastUS2,
            resource);

        await containers.CreateOrUpdateAsync(
            WaitUntil.Completed,
            containerOptions.Name,
            content,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Container '{Container}' created via ARM (partition key {PartitionKeyPath}, default TTL {Ttl}).",
            containerOptions.Name,
            containerOptions.PartitionKeyPath,
            containerOptions.DefaultTtlSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
    }
}
