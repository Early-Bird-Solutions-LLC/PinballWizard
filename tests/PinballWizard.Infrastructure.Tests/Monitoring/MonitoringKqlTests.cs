using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringKqlTests
{
    [Theory]
    [InlineData(MonitoringWindow.OneHour, 1)]
    [InlineData(MonitoringWindow.TwentyFourHours, 24)]
    [InlineData(MonitoringWindow.SevenDays, 168)]
    public void ToTimeSpan_MapsWindowToHours(MonitoringWindow w, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), MonitoringKql.ToTimeSpan(w));
    }

    [Fact]
    public void NormalizeCategories_FillsMissingWithZero_InCanonicalOrder()
    {
        var raw = new[]
        {
            new KeyValuePair<string, long>("InsufficientGrounding", 34),
            new KeyValuePair<string, long>("OutOfScope", 47),
            new KeyValuePair<string, long>("Bogus", 99), // unknown -> dropped
        };

        var result = MonitoringKql.NormalizeCategories(raw);

        Assert.Equal(RefusalCategories.All.Count, result.Count);
        Assert.Equal("OutOfScope", result[0].Category);
        Assert.Equal(47, result[0].Count);
        Assert.Equal("InsufficientGrounding", result[1].Category);
        Assert.Equal(34, result[1].Count);
        Assert.Equal("CostCeilingHit", result[5].Category);
        Assert.Equal(0, result[5].Count); // missing -> 0
        Assert.DoesNotContain(result, r => r.Category == "Bogus");
    }

    [Fact]
    public void FivexxRate_ScopesToConfiguredPathPrefix()
    {
        var kql = MonitoringKql.FivexxRate("/api/wizard/");
        Assert.Contains("/api/wizard/", kql);
        Assert.Contains("resultCode", kql);
    }
}
