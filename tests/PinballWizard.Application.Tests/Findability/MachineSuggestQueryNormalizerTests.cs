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

    [Fact]
    public void Tokenize_NameMadeOfIntentWords_KeepsTokens()
    {
        // "pinball wizard" is only QuestionIntentWords — stripping would empty
        // the query and never hit the index (Bugbot Medium).
        Assert.Equal(["pinball", "wizard"], MachineSuggestQueryNormalizer.Tokenize("pinball wizard"));
    }

    [Fact]
    public void Normalize_BareAbbreviation_DoesNotRequireTitleOverlap()
    {
        // Synonym / phonetic hits (AFM → Attack from Mars) have no lexical
        // overlap with the typed token — post-filter must stay off (Bugbot High).
        var normalized = MachineSuggestQueryNormalizer.Normalize("afm");

        Assert.Equal(["afm"], normalized.Tokens);
        Assert.False(normalized.RequireTitleTokenOverlap);
    }

    [Fact]
    public void Normalize_QuestionShaped_RequiresTitleOverlap()
    {
        var normalized = MachineSuggestQueryNormalizer.Normalize(
            "whats a good strategy for transformers");

        Assert.Equal(["transformers"], normalized.Tokens);
        Assert.True(normalized.RequireTitleTokenOverlap);
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

    [Fact]
    public void Tokenize_Contraction_DoesNotEmitDonPrefix()
    {
        // Splitting on apostrophe used to emit "don" from "don't", which
        // TitleMatchesQuery prefix-matched Donkey Kong (#785 Bugbot Medium).
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "don't tell me about jaws");

        Assert.Equal(["dont", "jaws"], tokens);
        Assert.DoesNotContain("don", tokens);
        Assert.False(
            MachineSuggestQueryNormalizer.TitleMatchesQuery("Donkey Kong", tokens));
        Assert.True(
            MachineSuggestQueryNormalizer.TitleMatchesQuery("Jaws", tokens));
    }

    [Fact]
    public void Normalize_QuestionWrappedAbbreviation_StillFlagsPreferLexical()
    {
        // Fluff stripped → PreferLexical true, but the service soft-filters so
        // synonym hits survive when lexical is empty (#785 Bugbot High).
        var normalized = MachineSuggestQueryNormalizer.Normalize(
            "whats a good strategy for afm");

        Assert.Equal(["afm"], normalized.Tokens);
        Assert.True(normalized.RequireTitleTokenOverlap);
    }

    [Fact]
    public void Tokenize_HyphenatedName_KeepsJoinedToken()
    {
        // Splitting on '-' used to drop length-1 "x" from "x-men" (#785 Bugbot).
        Assert.Equal(["x-men"], MachineSuggestQueryNormalizer.Tokenize("x-men"));
        Assert.True(
            MachineSuggestQueryNormalizer.TitleMatchesQuery("X-Men", ["x-men"]));
    }

    [Fact]
    public void Tokenize_StrategyWithPinballWizard_DropsStrategyFluff()
    {
        // All-intent remainder used to keep "strategy" alongside pinball/wizard,
        // so Invasion Strategy looked on-topic (#785 Bugbot High).
        var tokens = MachineSuggestQueryNormalizer.Tokenize(
            "whats a good strategy for pinball wizard");

        Assert.Equal(["pinball", "wizard"], tokens);
        Assert.False(
            MachineSuggestQueryNormalizer.TitleMatchesQuery("Invasion Strategy", tokens));
        Assert.True(
            MachineSuggestQueryNormalizer.TitleMatchesQuery("Pinball Wizard", tokens));
    }
}
