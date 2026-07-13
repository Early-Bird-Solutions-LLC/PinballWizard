using PinballWizard.Application.Resolution;
using Xunit;

namespace PinballWizard.Application.Tests.Resolution;

public class ResolutionContractsTests
{
    // static readonly to satisfy CA1861 (warnaserror in this repo)
    private static readonly string[] HotWheelsTokens = ["hot", "wheels"];

    [Fact]
    public void MachineVariant_Create_NormalizesTheKey()
    {
        var v = MachineVariant.Create("Hot-Wheels", VariantKind.CuratedAlias,
            machineId: "GRxyz-M1", manufacturerKey: "americanpinball", groupId: "GRxyz");

        Assert.Equal("hot wheels", v.Key);
        Assert.Equal(HotWheelsTokens, v.Tokens);
        Assert.Equal(VariantKind.CuratedAlias, v.Kind);
    }

    [Fact]
    public void MachineVariant_Create_SingleTokenVariant_IsFlagged()
    {
        // Single-token variants are eligible for EXACT evidence only — this flag is what
        // the resolver checks instead of excluding them from the index (the 1977 Stern
        // "Pinball" once matched 172 documents).
        var v = MachineVariant.Create("Pinball", VariantKind.FullTitle, "G1-M1", "stern", "G1");
        Assert.True(v.IsSingleToken);

        var multi = MachineVariant.Create("Hot Wheels", VariantKind.FullTitle, "G2-M1", "americanpinball", "G2");
        Assert.False(multi.IsSingleToken);
    }

    [Fact]
    public void MachineVariant_Create_EmptyText_Throws()
        => Assert.ThrowsAny<ArgumentException>(() =>
            MachineVariant.Create("---", VariantKind.FullTitle, "G1-M1", "stern", "G1"));
}
