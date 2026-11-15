using PinballWizard.Infrastructure.Integrations.PinTips;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.PinTips;

public sealed class PinTipsParserTests
{
    [Fact]
    public void Parse_KeepsACompleteTipAndSkipsABlankOne()
    {
        const string json = """
            [
              {"tipId": 2, "opdbId": "G5pe4", "category": "general", "voteTotal": 15, "text": "Shoot the castle."},
              {"tipId": 3, "opdbId": "", "category": "general", "voteTotal": 1, "text": "Missing machine."},
              {"tipId": 4, "opdbId": "G5pe4", "category": "general", "voteTotal": 1, "text": "   "}
            ]
            """;

        var tip = Assert.Single(PinTipsParser.Parse(json));
        Assert.Equal(2, tip.TipId);
        Assert.Equal("G5pe4", tip.OpdbId);
        Assert.Equal("Shoot the castle.", tip.Text);
        Assert.Equal(15, tip.VoteTotal);
    }

    [Fact]
    public void Parse_ObjectDocument_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PinTipsParser.Parse("""{"tips":[]}"""));
        Assert.Contains("JSON array", ex.Message, StringComparison.Ordinal);
    }
}
