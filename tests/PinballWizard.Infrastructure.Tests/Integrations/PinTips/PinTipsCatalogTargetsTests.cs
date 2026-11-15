using PinballWizard.Infrastructure.Integrations.PinTips;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.PinTips;

public sealed class PinTipsCatalogTargetsTests
{
    private static readonly PinTipsIndexTarget[] MedievalMadness =
    [
        new("G5pe4-Mpro", "Medieval Madness (Pro)", "Williams"),
        new("G5pe4-Mle", "Medieval Madness (LE)", "Williams"),
    ];

    [Fact]
    public void Resolve_GroupId_FansOutToEveryMachineInTheGroup()
    {
        var targets = PinTipsCatalogTargets.Resolve("G5pe4", MedievalMadness);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.MachineId == "G5pe4-Mpro");
        Assert.Contains(targets, t => t.MachineId == "G5pe4-Mle");
    }

    [Fact]
    public void Resolve_MachineId_KeepsOnlyThatMachine()
    {
        var targets = PinTipsCatalogTargets.Resolve("G5pe4-Mpro", MedievalMadness);

        var only = Assert.Single(targets);
        Assert.Equal("G5pe4-Mpro", only.MachineId);
    }

    [Fact]
    public void Resolve_UnknownGroup_ReturnsNothing()
    {
        Assert.Empty(PinTipsCatalogTargets.Resolve("Gnone", []));
    }
}
