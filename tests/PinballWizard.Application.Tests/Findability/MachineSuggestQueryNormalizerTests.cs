using PinballWizard.Application.Findability;
using Xunit;

namespace PinballWizard.Application.Tests.Findability;

// Behavior tests for MachineSuggestQueryNormalizer (#785).
// Asserts the stopword / max-length filter that keeps question-shaped
// typeahead queries from ranking Fore / No Good Gofers / Invasion Strategy.
public sealed class MachineSuggestQueryNormalizerTests
{
    [Fact]
    public void Tokenize_QuestionShaped_StripsStopwordsKeepsEntityAndShorterLeftovers()
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for transformers");

        Assert.Equal(["strategy", "transformers"], tokens);
    }

    [Fact]
    public void Tokenize_BareName_KeepsSignificantToken()
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize("Godzilla");

        Assert.Equal(["godzilla"], tokens);
    }

    [Fact]
    public void Tokenize_AllStopwords_ReturnsEmpty()
    {
        Assert.Empty(MachineSuggestQueryNormalizer.Tokenize("whats a good for"));
    }

    [Theory]
    [InlineData("Transformers", true)]
    [InlineData("Transformers: More Than Meets the Eye", true)]
    [InlineData("Invasion Strategy", false)]
    [InlineData("No Good Gofers", false)]
    [InlineData("Fore", false)]
    [InlineData("Quest for Glory", false)]
    [InlineData("Avatar: The Battle for Pandora", false)]
    public void TitleMatchesQuery_QuestionShaped_RequiresMaxLengthToken(
        string title, bool expected)
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for transformers");

        Assert.Equal(expected, MachineSuggestQueryNormalizer.TitleMatchesQuery(title, tokens));
    }

    [Fact]
    public void TitleMatchesQuery_SingleTokenStrategy_AllowsInvasionStrategy()
    {
        // Bare name typing must still find Invasion Strategy via "strategy".
        var tokens = MachineSuggestQueryNormalizer.Tokenize("strategy");

        Assert.True(MachineSuggestQueryNormalizer.TitleMatchesQuery("Invasion Strategy", tokens));
    }

    [Fact]
    public void BuildSearchText_JoinsTokens()
    {
        Assert.Equal(
            "strategy transformers",
            MachineSuggestQueryNormalizer.BuildSearchText(["strategy", "transformers"]));
        Assert.Equal(string.Empty, MachineSuggestQueryNormalizer.BuildSearchText([]));
    }
}
