using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

/// <summary>
/// Tests for <see cref="CosmosOptions"/> defaults. The default container
/// list is load-bearing: <see cref="CosmosBootstrapper"/> uses it on
/// post-deploy smoke-tests (<c>--ensure-cosmos-containers</c>) to create
/// the containers the repositories already write to (the names are
/// hardcoded in the repository registrations). A drift between
/// CosmosOptions defaults and the repository names would silently leave
/// repositories writing to non-existent containers; these tests pin
/// every name and partition-key path against ADR 0011.
/// </summary>
public sealed class CosmosOptionsTests
{
    [Fact]
    public void Defaults_DatabaseName_IsPinwiz()
    {
        var options = new CosmosOptions();
        Assert.Equal("pinwiz", options.DatabaseName);
    }

    [Fact]
    public void Defaults_Containers_IncludesMachinesWithCorrectPartitionKey()
    {
        var options = new CosmosOptions();

        var machines = Assert.Single(options.Containers, c => c.Name == "machines");
        Assert.Equal("/manufacturer", machines.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_IncludesIngestionSourcesWithCorrectPartitionKey()
    {
        var options = new CosmosOptions();

        var ingestion = Assert.Single(options.Containers, c => c.Name == "ingestion_sources");
        Assert.Equal("/partitionKey", ingestion.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_HasExactlyTheTwoPhase1Containers()
    {
        // Pin the count so a future addition that drifts from the repository
        // registrations (which would silently leave the new container missing
        // partition-key validation) trips this test as a flag.
        var options = new CosmosOptions();
        Assert.Equal(2, options.Containers.Count);
    }

    [Fact]
    public void Defaults_AccountEndpoint_IsNull()
    {
        // Optional by design — Aspire's AddAzureCosmosClient supplies the
        // CosmosClient via TryAddSingleton, in which case AccountEndpoint
        // is unused. Leaving it null in the default lets standalone CLI
        // runs (no Aspire, no Cosmos config) skip the registration without
        // failing data-annotation validation.
        var options = new CosmosOptions();
        Assert.Null(options.AccountEndpoint);
    }
}
