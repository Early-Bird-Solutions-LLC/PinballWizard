using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Core.Tests.Models;

public sealed class ScrapeRunIdTests
{
    [Fact]
    public void For_BuildsDeterministicIdFromSourceAndUtcRunAt()
    {
        var runAt = new DateTimeOffset(2026, 6, 21, 4, 0, 3, TimeSpan.Zero);
        Assert.Equal("opdb_20260621040003000Z", ScrapeRunId.For("opdb", runAt));
    }

    [Fact]
    public void For_NormalizesToUtc_BeforeFormatting()
    {
        // 23:30 at +05:00 == 18:30 UTC
        var runAt = new DateTimeOffset(2026, 6, 21, 23, 30, 0, TimeSpan.FromHours(5));
        Assert.Equal("stern_20260621183000000Z", ScrapeRunId.For("stern", runAt));
    }
}
