using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class CronExpressionFormatterTests
{
    [Theory]
    [InlineData("0 2 * * *",   "Daily at 2:00 AM UTC")]
    [InlineData("0 14 * * *",  "Daily at 2:00 PM UTC")]
    [InlineData("0 0 * * *",   "Daily at 12:00 AM UTC")]
    [InlineData("0 12 * * *",  "Daily at 12:00 PM UTC")]
    [InlineData("0 3 * * 0",   "Sundays at 3:00 AM UTC")]
    [InlineData("0 10 * * 1",  "Mondays at 10:00 AM UTC")]
    [InlineData("0 11 * * 0",  "Sundays at 11:00 AM UTC")]
    [InlineData("0 3 * * 7",   "Sundays at 3:00 AM UTC")]   // 7 = Sunday alias
    [InlineData("*/15 * * * *", "Every 15 minutes")]
    [InlineData("0 */6 * * *", "Every 6 hours")]
    [InlineData("0 0 1 * *",   "Monthly on day 1 at 12:00 AM UTC")]
    [InlineData("* * * * *",   "Every minute")]
    public void Format_KnownPatterns_ReturnHumanReadable(string expression, string expected)
    {
        Assert.Equal(expected, CronExpressionFormatter.Format(expression));
    }

    [Fact]
    public void Format_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CronExpressionFormatter.Format(null));
    }

    [Fact]
    public void Format_FourFieldExpression_ReturnsFallback()
    {
        // Not a standard 5-field cron — return as-is
        Assert.Equal("0 2 * *", CronExpressionFormatter.Format("0 2 * *"));
    }

    [Fact]
    public void Format_UnrecognisedPattern_ReturnsFallback()
    {
        // Valid 5-field cron we don't handle specially — return raw
        Assert.Equal("5 4 * * 1,5", CronExpressionFormatter.Format("5 4 * * 1,5"));
    }

    [Fact]
    public void Format_WhitespaceOnly_ReturnsFallback()
    {
        Assert.Equal("   ", CronExpressionFormatter.Format("   "));
    }
}
