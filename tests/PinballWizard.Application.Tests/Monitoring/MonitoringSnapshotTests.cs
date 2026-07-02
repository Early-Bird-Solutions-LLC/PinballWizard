using PinballWizard.Application.Monitoring;
using Xunit;

namespace PinballWizard.Application.Tests.Monitoring;

public sealed class MonitoringSnapshotTests
{
    private static readonly string[] ExpectedCategories =
    [
        "OutOfScope", "InsufficientGrounding", "NoCitation",
        "LowModelConfidence", "HarmfulContent", "CostCeilingHit",
    ];

    [Fact]
    public void NullMetric_MeansUnavailable_NotZero()
    {
        var snap = new MonitoringSnapshot
        {
            Window = MonitoringWindow.TwentyFourHours,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            LatencyP95Ms = 2310,
            // FivexxRatePercent intentionally left null => unavailable
        };

        Assert.Equal(2310, snap.LatencyP95Ms);
        Assert.Null(snap.FivexxRatePercent);
    }

    [Fact]
    public void RefusalCategories_All_AreTheCanonicalSixInOrder()
    {
        Assert.Equal(ExpectedCategories, RefusalCategories.All);
    }
}
