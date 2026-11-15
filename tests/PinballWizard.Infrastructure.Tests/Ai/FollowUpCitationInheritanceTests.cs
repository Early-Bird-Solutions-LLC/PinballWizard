using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

public sealed class FollowUpCitationInheritanceTests
{
    private static readonly ConversationTurn Prior = new(
        "what are the rules for Medieval Madness",
        "Cast spell modes to progress.",
        [
            new Citation(
                "Medieval Madness Manual",
                "https://example.com/mm-manual.pdf",
                MachineId: "G50L5-MQrZv"),
        ]);

    [Theory]
    [InlineData("How many balls in multiball?")]
    [InlineData("What about the wizard mode?")]
    [InlineData("Does it have a multiball?")]
    public void ShouldInherit_ClarifyingFollowUps_ReturnsTrue(string followUp)
    {
        Assert.True(FollowUpCitationInheritance.ShouldInherit(followUp, [Prior]));
    }

    [Theory]
    [InlineData("show me games similar to Medieval Madness")]
    [InlineData("Recommend machines like Medieval Madness")]
    [InlineData("What are alternatives to Medieval Madness?")]
    [InlineData("Compare Medieval Madness vs Attack from Mars")]
    [InlineData("games like Medieval Madness")]
    // Sentence-edge / "?"-attached compare-family markers (#780 Bugbot).
    [InlineData("How do they compare?")]
    [InlineData("Compare Medieval Madness and Attack from Mars?")]
    [InlineData("vs Attack from Mars")]
    [InlineData("Medieval Madness versus?")]
    public void ShouldInherit_NewIntentFollowUps_ReturnsFalse(string followUp)
    {
        Assert.False(FollowUpCitationInheritance.ShouldInherit(followUp, [Prior]));
    }

    [Fact]
    public void ShouldInherit_EmptyHistory_ReturnsFalse()
    {
        Assert.False(FollowUpCitationInheritance.ShouldInherit("How many balls?", []));
    }

    [Fact]
    public void ShouldInherit_BlankQuestion_ReturnsFalse()
    {
        Assert.False(FollowUpCitationInheritance.ShouldInherit("  ", [Prior]));
    }
}
