namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Abstraction over the "ensure Cosmos database + containers exist"
/// operation. Two implementations exist for the two distinct Cosmos
/// auth models the project supports:
/// <list type="bullet">
///   <item>
///     <see cref="DataPlaneCosmosProvisioner"/> — uses the
///     <c>Microsoft.Azure.Cosmos</c> SDK against the data-plane
///     endpoint. Works only when the underlying
///     <see cref="Microsoft.Azure.Cosmos.CosmosClient"/> is built from
///     a connection string with a master-key (the Aspire preview
///     emulator path); Cosmos's data-plane RBAC does NOT include
///     schema-mutation actions for AAD-authed clients.
///   </item>
///   <item>
///     <see cref="ArmCosmosProvisioner"/> — uses
///     <c>Azure.ResourceManager.CosmosDB</c> against the management
///     endpoint. Works for any AAD principal that holds Azure RBAC
///     write permissions on the account (subscription Owner /
///     Contributor / Cosmos DB Operator). This is the deployed-Cosmos
///     path.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// The split is structural, not stylistic: schema CRUD in Cosmos is
/// architecturally a control-plane concern. The data-plane SDK appears
/// to expose <c>CreateDatabaseIfNotExistsAsync</c> for historical
/// (master-key-era) reasons — when AAD is used, those calls bridge to
/// ARM internally and the ARM-bridge auth check rejects every
/// Cosmos-data-plane role definition because the action namespace
/// genuinely doesn't model schema mutations. This was confirmed by
/// PR #62's failed attempt to add <c>sqlDatabases/*</c> to a custom
/// data-plane role: Azure rejected the deploy with "not a valid SQL
/// data action."
/// </remarks>
public interface ICosmosProvisioner
{
    /// <summary>
    /// Ensures the configured database and every configured container
    /// exists with the correct partition-key path. Throws if a
    /// container exists with a different partition-key path than
    /// configured (drift is fatal — operator must reconcile).
    /// </summary>
    Task EnsureDatabaseAndContainersAsync(
        string databaseName,
        IReadOnlyList<CosmosContainerOptions> containers,
        CancellationToken cancellationToken);
}
