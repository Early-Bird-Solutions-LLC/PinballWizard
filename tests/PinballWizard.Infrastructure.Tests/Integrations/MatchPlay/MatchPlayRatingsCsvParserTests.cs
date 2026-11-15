using PinballWizard.Infrastructure.Integrations.MatchPlay;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.MatchPlay;

public sealed class MatchPlayRatingsCsvParserTests
{
    private const string Header =
        "Name,User ID,IFPA ID,Rating,RD,Lower Bound,Last Rating Period (Data from: 2026-11-14),Rating class";

    [Fact]
    public void Parse_MapsRows_IncludingQuotedNameAndEmptyIfpa()
    {
        var csv = string.Join('\n',
            Header,
            "Ada Lovelace,101,42,1850.5,45.2,1760.1,2026-11-14,3",
            "\"Jones, Craig\",202,,2100.0,30.0,2040.0,2026-11-14,1",
            "skip-me,0,1,100,10,80,2026-11-14,9");

        var rows = MatchPlayRatingsCsvParser.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ada Lovelace", rows[0].Name);
        Assert.Equal(101, rows[0].UserId);
        Assert.Equal(42, rows[0].IfpaId);
        Assert.Equal(1850.5, rows[0].Rating);
        Assert.Equal(45.2, rows[0].Rd);
        Assert.Equal(1760.1, rows[0].LowerBound);
        Assert.Equal(3, rows[0].RatingClass);

        Assert.Equal("Jones, Craig", rows[1].Name);
        Assert.Equal(202, rows[1].UserId);
        Assert.Null(rows[1].IfpaId);
        Assert.Equal(2100.0, rows[1].Rating);
    }

    [Fact]
    public void Parse_MissingUserIdHeader_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MatchPlayRatingsCsvParser.Parse("Name,Rating\nAda,1"));
        Assert.Contains("User ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_HeaderOnly_ThrowsEmptyIndex()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MatchPlayRatingsCsvParser.Parse(Header + "\n"));
        Assert.Contains("zero rows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
