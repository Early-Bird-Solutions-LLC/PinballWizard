namespace PinballWizard.PerfMetrics.Tests;

public sealed class LighthouseReportTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-lhr.json"));

    [Fact]
    public void Parse_MapsCategoriesToZeroToHundred_AndVitalsToRawNumericValues()
    {
        var s = LighthouseReport.Parse(Fixture(),
            page: "/wizard", environment: "synthetic",
            commitSha: "abc1234", runTimestampUtc: "2026-07-08T12:00:00Z");

        Assert.Equal("/wizard", s.Page);
        Assert.Equal("synthetic", s.Environment);
        Assert.Equal(90d, s.Performance);      // 0.90 * 100
        Assert.Equal(0.169d, s.Cls, precision: 3); // raw numericValue, NOT scaled
        Assert.True(s.Lcp > 0);                 // ms
        Assert.Equal("abc1234", s.CommitSha);
    }
}
