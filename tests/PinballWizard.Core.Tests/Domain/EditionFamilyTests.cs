using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Core.Tests.Domain;

public sealed class EditionFamilyTests
{
    private static Machine MakeMachine(string id, string? groupId, int? year) => new()
    {
        Id = id,
        PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball",
        Title = "Some Game",
        GroupId = groupId,
        Year = year,
    };

    [Fact]
    public void IsEditionFamily_SameGroupIdSameYear_ReturnsTrue()
    {
        var pro = MakeMachine("GweeP-MW95j", "GweeP", 2021);
        var premium = MakeMachine("GweeP-Ml9pZ", "GweeP", 2021);

        Assert.True(EditionFamily.IsEditionFamily([pro, premium]));
    }

    [Fact]
    public void IsEditionFamily_SameGroupIdDifferentYear_ReturnsFalse()
    {
        // An unrelated reissue/remake can reuse the same group segment in a
        // different year — that is NOT the same edition family.
        var original = MakeMachine("ABCD-1", "ABCD", 1992);
        var remake = MakeMachine("ABCD-2", "ABCD", 2023);

        Assert.False(EditionFamily.IsEditionFamily([original, remake]));
    }

    [Fact]
    public void IsEditionFamily_DifferentGroupId_ReturnsFalse()
    {
        var sternGodzilla = MakeMachine("GweeP-MW95j", "GweeP", 2021);
        var segaGodzilla = MakeMachine("G4O1L-abc12", "G4O1L", 1998);

        Assert.False(EditionFamily.IsEditionFamily([sternGodzilla, segaGodzilla]));
    }

    [Fact]
    public void IsEditionFamily_SingleCandidateWithGroupIdAndYear_ReturnsTrue()
    {
        // A lone candidate that belongs to a group still counts — matches
        // current DocumentLinker usage, which runs a singleton through this
        // check to tag EditionScope.SingleEdition vs. FranchiseWide correctly.
        var solo = MakeMachine("GweeP-MW95j", "GweeP", 2021);

        Assert.True(EditionFamily.IsEditionFamily([solo]));
    }

    [Fact]
    public void IsEditionFamily_EmptyList_ReturnsFalse()
    {
        Assert.False(EditionFamily.IsEditionFamily([]));
    }

    [Fact]
    public void IsEditionFamily_NullGroupId_ReturnsFalse()
    {
        var a = MakeMachine("A-1", null, 2021);
        var b = MakeMachine("A-2", null, 2021);

        Assert.False(EditionFamily.IsEditionFamily([a, b]));
    }

    [Fact]
    public void IsEditionFamily_NullYear_ReturnsFalse()
    {
        var a = MakeMachine("A-1", "GroupA", null);
        var b = MakeMachine("A-2", "GroupA", null);

        Assert.False(EditionFamily.IsEditionFamily([a, b]));
    }
}
