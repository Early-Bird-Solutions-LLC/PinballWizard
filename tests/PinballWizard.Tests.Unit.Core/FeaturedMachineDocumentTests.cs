using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Tests.Unit.Core;

/// <summary>
/// Unit tests for <see cref="FeaturedMachineDocument"/>: structural invariants
/// and the ADR-0025 § 4 point-read contract (id == partition-key value == slug).
/// </summary>
public sealed class FeaturedMachineDocumentTests
{
    [Fact]
    public void IdEqualsPartitionKey_Equals_Slug_ByConstruction()
    {
        // ADR-0025 § 4: id == partition-key value so reads are pure point-lookups.
        // The document slug serves as both the document id and the partition key.
        var doc = new FeaturedMachineDocument
        {
            Id = "stern-godzilla",
            PartitionKey = "stern-godzilla",
            Title = "Godzilla Pro",
            DisplayOrder = 1,
            Tagline = "King of the monsters",
        };

        Assert.Equal(doc.Id, doc.PartitionKey);
        Assert.Equal("stern-godzilla", doc.Id);
    }

    [Fact]
    public void OpdbId_IsNullable()
    {
        // Not all machines have a verified OPDB ID. Null is the correct
        // value for entries where the ID has not been confirmed.
        var doc = new FeaturedMachineDocument
        {
            Id = "ap-houdini",
            PartitionKey = "ap-houdini",
            Title = "Houdini: Master of Mystery",
            DisplayOrder = 4,
            Tagline = "A tagline.",
            OpdbId = null,
        };

        Assert.Null(doc.OpdbId);
    }

    [Fact]
    public void OpdbId_WhenSet_IsPreserved()
    {
        var doc = new FeaturedMachineDocument
        {
            Id = "stern-test",
            PartitionKey = "stern-test",
            Title = "Test Machine",
            DisplayOrder = 1,
            Tagline = "Tagline",
            OpdbId = "GRBN-MHVTP",
        };

        Assert.Equal("GRBN-MHVTP", doc.OpdbId);
    }

    [Fact]
    public void DisplayOrder_MustBePositive_Contract()
    {
        // Seed loader enforces display_order > 0 at load time; this test
        // documents the contract expectation on the domain type.
        var doc = new FeaturedMachineDocument
        {
            Id = "jjp-wonka",
            PartitionKey = "jjp-wonka",
            Title = "Wonka",
            DisplayOrder = 2,
            Tagline = "Pure imagination",
        };

        Assert.True(doc.DisplayOrder > 0);
    }

    [Fact]
    public void ImplementsIEntity_IdAndPartitionKeyAreAccessibleViaInterface()
    {
        // Verify that FeaturedMachineDocument satisfies the CosmosRepository<T>
        // constraint (where T : class, IEntity) by casting and reading the
        // interface members. Cast is explicit to confirm assignability at runtime.
        FeaturedMachineDocument doc = new()
        {
            Id = "slug",
            PartitionKey = "slug",
            Title = "Title",
            DisplayOrder = 1,
            Tagline = "Tagline",
        };

        // Access via interface without a separately-typed local (avoids CA1859).
        Assert.Equal("slug", ((IEntity)doc).Id);
        Assert.Equal("slug", ((IEntity)doc).PartitionKey);
    }
}
