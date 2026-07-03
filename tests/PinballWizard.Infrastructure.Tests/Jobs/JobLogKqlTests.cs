using PinballWizard.Infrastructure.Jobs;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

public sealed class JobLogKqlTests
{
    private static string Build(string? search) =>
        JobLogKql.BuildExecutionLogsQuery(
            "pinwiz-job-linker-buutj-29715960",
            System.DateTimeOffset.UnixEpoch, System.DateTimeOffset.UnixEpoch.AddMinutes(5),
            1000, search);

    [Fact]
    public void NoSearch_OmitsContainsClause()
    {
        var kql = Build(null);
        Assert.DoesNotContain("contains", kql, System.StringComparison.Ordinal);
        Assert.Contains("take 1001", kql, System.StringComparison.Ordinal); // maxLines + 1
    }

    [Fact]
    public void WithSearch_AddsCaseInsensitiveContainsClause()
    {
        var kql = Build("Godzilla");
        Assert.Contains("Log_s contains @'Godzilla'", kql, System.StringComparison.Ordinal);
    }

    [Fact]
    public void WithSearch_EscapesSingleQuotesByDoubling()
    {
        var kql = Build("O'Brien");
        Assert.Contains("Log_s contains @'O''Brien'", kql, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("a'b", "a''b")]
    [InlineData("line\r\nbreak", "linebreak")]
    public void KqlLiteral_DoublesQuotes_AndStripsNewlines(string? input, string expected) =>
        Assert.Equal(expected, JobLogSafe.KqlLiteral(input));

    [Fact]
    public void MaxLinesCap_IsTenThousand() => Assert.Equal(10000, JobLogKql.MaxLinesCap);
}
