using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class DataPlaneCosmosProvisionerTests
{
    [Fact]
    public void BuildContainerProperties_CarriesPartitionKey_Ttl_AndIndexPaths()
    {
        var opts = new CosmosContainerOptions
        {
            Name = "scraped_documents_raw",
            PartitionKeyPath = "/document_id",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/run_id/?"],
                ExcludedPaths = ["/*"],
            },
        };

        var props = DataPlaneCosmosProvisioner.BuildContainerProperties(opts);

        Assert.Equal("/document_id", props.PartitionKeyPath);
        Assert.Contains("/run_id/?", props.IndexingPolicy.IncludedPaths.Select(p => p.Path));
    }

    [Fact]
    public async Task EnsureContainer_ReplacesWhenIndexDrifts()
    {
        // existing container returned by CreateContainerIfNotExists has an OUTDATED index policy
        var drifted = new ContainerProperties("scraped_documents_raw", "/document_id");
        drifted.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/document_id/?" });
        drifted.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });

        var (provisioner, database, container) = ArrangeProvisioner(drifted);

        await provisioner.EnsureDatabaseAndContainersAsync("pinwiz", [ScrapedDocsOpts()], CancellationToken.None);

        await container.Received(1).ReplaceContainerAsync(
            Arg.Is<ContainerProperties>(p => p.IndexingPolicy.IncludedPaths.Any(x => x.Path == "/run_id/?")),
            Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureContainer_DoesNotReplaceWhenMatching()
    {
        var matching = DataPlaneCosmosProvisioner.BuildContainerProperties(ScrapedDocsOpts());
        var (provisioner, _, container) = ArrangeProvisioner(matching);

        await provisioner.EnsureDatabaseAndContainersAsync("pinwiz", [ScrapedDocsOpts()], CancellationToken.None);

        await container.DidNotReceive().ReplaceContainerAsync(
            Arg.Any<ContainerProperties>(), Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IndexingPolicyMatches_TrueWhenActualHasCosmosSystemEtagExclusion()
    {
        // Cosmos auto-adds /\"_etag\"/? to ExcludedPaths; the configured expected
        // list only contains "/*". The match must still return true.
        var actual = new IndexingPolicy();
        actual.IncludedPaths.Add(new IncludedPath { Path = "/document_id/?" });
        actual.IncludedPaths.Add(new IncludedPath { Path = "/run_id/?" });
        actual.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        actual.ExcludedPaths.Add(new ExcludedPath { Path = "/\"_etag\"/?" });
        var expected = new CosmosIndexingPolicyOptions
        {
            IncludedPaths = ["/document_id/?", "/run_id/?"],
            ExcludedPaths = ["/*"],
        };

        Assert.True(DataPlaneCosmosProvisioner.IndexingPolicyMatches(actual, expected));
    }

    [Fact]
    public void TtlMatches_TrueWhenBothNull()
    {
        Assert.True(DataPlaneCosmosProvisioner.TtlMatches(null, null));
    }

    [Fact]
    public void TtlMatches_TrueWhenActualIsEmulatorSentinel_AndExpectedIsNull()
    {
        // Aspire vnext-preview emulator reports DefaultTimeToLive = -2 when no TTL is configured.
        // Treat -2 as equivalent to null so --ensure-cosmos-containers is idempotent locally.
        Assert.True(DataPlaneCosmosProvisioner.TtlMatches(-2, null));
    }

    [Fact]
    public void TtlMatches_FalseWhenActualDiffersFromExpected()
    {
        Assert.False(DataPlaneCosmosProvisioner.TtlMatches(3600, null));
        Assert.False(DataPlaneCosmosProvisioner.TtlMatches(null, 3600));
        Assert.False(DataPlaneCosmosProvisioner.TtlMatches(-2, 3600));
    }

    private static CosmosContainerOptions ScrapedDocsOpts() => new()
    {
        Name = "scraped_documents_raw",
        PartitionKeyPath = "/document_id",
        IndexingPolicy = new CosmosIndexingPolicyOptions
        {
            IncludedPaths = ["/document_id/?", "/run_id/?"],
            ExcludedPaths = ["/*"],
        },
    };

    private static (DataPlaneCosmosProvisioner, Database, Container) ArrangeProvisioner(ContainerProperties existing)
    {
        var client = Substitute.For<CosmosClient>();
        var database = Substitute.For<Database>();
        var container = Substitute.For<Container>();

        var dbResponse = Substitute.For<DatabaseResponse>();
        dbResponse.Database.Returns(database);
        client.CreateDatabaseIfNotExistsAsync(Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>()).Returns(dbResponse);

        var containerResponse = Substitute.For<ContainerResponse>();
        containerResponse.Resource.Returns(existing);
        database.CreateContainerIfNotExistsAsync(Arg.Any<ContainerProperties>(), Arg.Any<int?>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>()).Returns(containerResponse);
        database.GetContainer("scraped_documents_raw").Returns(container);
        container.ReplaceContainerAsync(Arg.Any<ContainerProperties>(),
            Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>()).Returns(containerResponse);

        var provisioner = new DataPlaneCosmosProvisioner(client, NullLogger<DataPlaneCosmosProvisioner>.Instance);
        return (provisioner, database, container);
    }
}
