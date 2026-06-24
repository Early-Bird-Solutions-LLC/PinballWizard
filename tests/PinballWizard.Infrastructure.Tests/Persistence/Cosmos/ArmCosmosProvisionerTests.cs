using Azure.ResourceManager.CosmosDB.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class ArmCosmosProvisionerTests
{
    [Fact]
    public void BuildContainerContent_CarriesPartitionKey_Ttl_AndIndexPaths()
    {
        var opts = new CosmosContainerOptions
        {
            Name = "scraped_documents_raw",
            PartitionKeyPath = "/document_id",
            DefaultTtlSeconds = null,
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/run_id/?"],
                ExcludedPaths = ["/*"],
            },
        };

        var content = ArmCosmosProvisioner.BuildContainerContent(opts);

        Assert.Equal("/document_id", content.Resource.PartitionKey.Paths[0]);
        Assert.Equal(CosmosDBPartitionKind.Hash, content.Resource.PartitionKey.Kind);
        Assert.Contains("/run_id/?", content.Resource.IndexingPolicy.IncludedPaths.Select(p => p.Path));
        Assert.Contains("/*", content.Resource.IndexingPolicy.ExcludedPaths.Select(p => p.Path));
        Assert.Null(content.Resource.DefaultTtl);
    }

    [Fact]
    public void IndexingPolicyMatches_FalseWhenIncludedPathsDiffer()
    {
        var actual = new CosmosDBIndexingPolicy();
        actual.IncludedPaths.Add(new CosmosDBIncludedPath { Path = "/document_id/?" });
        actual.ExcludedPaths.Add(new CosmosDBExcludedPath { Path = "/*" });
        var expected = new CosmosIndexingPolicyOptions
        {
            IncludedPaths = ["/document_id/?", "/run_id/?"],
            ExcludedPaths = ["/*"],
        };

        Assert.False(ArmCosmosProvisioner.IndexingPolicyMatches(actual, expected));
    }

    [Fact]
    public void TtlMatches_TrueOnlyOnExactNullableEquality()
    {
        Assert.True(ArmCosmosProvisioner.TtlMatches(null, null));
        Assert.False(ArmCosmosProvisioner.TtlMatches(-2, null));
        Assert.True(ArmCosmosProvisioner.TtlMatches(7776000, 7776000));
    }
}
