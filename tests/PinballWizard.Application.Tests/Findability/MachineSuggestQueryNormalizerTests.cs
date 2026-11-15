using PinballWizard.Application.Findability;
using Xunit;

namespace PinballWizard.Application.Tests.Findability;

// Behavior tests for MachineSuggestQueryNormalizer (#785).
// Asserts stopword / question-intent stripping that keeps question-shaped
// typeahead queries from ranking Fore / No Good Gofers / Invasion Strategy,
// without dropping short titles (Jaws) or mid-typing prefixes (transform…).
public sealed class MachineSuggestQueryNormalizerTests
{
    [Fact]
    public void Tokenize_QuestionShaped_StripsStopwordsAndIntentWords()
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for transformers");

        // strategy is question-intent and stripped because transformers remains.
        Assert.Equal(["transformers"], tokens);
    }

    [Fact]
    public void Tokenize_BareName_KeepsSignificantToken()
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize("Godzilla");

        Assert.Equal(["godzilla"], tokens);
    }

    [Fact]
    public void Tokenize_SoleIntentWord_KeptForNameTyping()
    {
        // Bare "strategy" must still find Invasion Strategy.
        Assert.Equal(["strategy"], MachineSuggestQueryNormalizer.Tokenize("strategy"));
    }

    [Fact]
    public void Tokenize_AllStopwords_ReturnsEmpty()
    {
        Assert.Empty(MachineSuggestQueryNormalizer.Tokenize("whats a good for"));
    }

    [Fact]
    public void Tokenize_ShortTitleWithIntentFluff_KeepsShortEntity()
    {
        // Bugbot case: intent word longer than entity must not erase "jaws".
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for jaws");

        Assert.Equal(["jaws"], tokens);
    }

    [Theory]
    [InlineData("Transformers", true)]
    [InlineData("Transformers: More Than Meets the Eye", true)]
    [InlineData("Invasion Strategy", false)]
    [InlineData("No Good Gofers", false)]
    [InlineData("Fore", false)]
    [InlineData("Quest for Glory", false)]
    [InlineData("Avatar: The Battle for Pandora", false)]
    public void TitleMatchesQuery_QuestionShaped_RequiresEntityToken(
        string title, bool expected)
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for transformers");

        Assert.Equal(expected, MachineSuggestQueryNormalizer.TitleMatchesQuery(title, tokens));
    }

    [Fact]
    public void TitleMatchesQuery_SingleTokenStrategy_AllowsInvasionStrategy()
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize("strategy");

        Assert.True(MachineSuggestQueryNormalizer.TitleMatchesQuery("Invasion Strategy", tokens));
    }

    [Fact]
    public void TitleMatchesQuery_PrefixWhileTyping_MatchesTransformers()
    {
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for transform");

        Assert.True(MachineSuggestQueryNormalizer.TitleMatchesQuery("Transformers", tokens));
        Assert.False(MachineSuggestQueryNormalizer.TitleMatchesQuery("Invasion Strategy", tokens));
    }

    [Fact]
    public void BuildSearchText_JoinsTokens()
    {
        Assert.Equal(
            "transformers",
            MachineSuggestQueryNormalizer.BuildSearchText(["transformers"]));
        Assert.Equal(string.Empty, MachineSuggestQueryNormalizer.BuildSearchText([]));
    }
}
