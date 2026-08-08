using PinballWizard.Api.Pipeline;
using Xunit;

namespace PinballWizard.Api.Tests;

public class QueryPreprocessorTests
{
    private readonly QueryPreprocessor _preprocessor = new();

    [Theory]
    [InlineData("How do I fix the left flipper?", QueryIntent.Repair)]
    [InlineData("The bumper is broken and needs repair", QueryIntent.Repair)]
    [InlineData("How to play Medieval Madness", QueryIntent.Rules)]
    [InlineData("What are the rules and scoring modes?", QueryIntent.Rules)]
    [InlineData("Troubleshoot my display not working", QueryIntent.Troubleshooting)]
    [InlineData("When was Attack From Mars manufactured?", QueryIntent.History)]
    [InlineData("Compare Medieval Madness versus Attack From Mars", QueryIntent.Comparison)]
    [InlineData("Best strategy tips for high scores", QueryIntent.Strategy)]
    [InlineData("Where to find replacement part number for switch?", QueryIntent.Parts)]
    [InlineData("How to calibrate the playfield level?", QueryIntent.Setup)]
    [InlineData("Tell me about pinball", QueryIntent.General)]
    public void DetectIntent_CorrectlyClassifiesQueries(string query, QueryIntent expected)
    {
        var result = QueryPreprocessor.DetectIntent(query);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractGameSlugs_FindsKnownGameInQuery()
    {
        var slugs = QueryPreprocessor.ExtractGameSlugs("How do I fix Medieval Madness flipper?", null);
        Assert.Contains("medieval-madness", slugs);
    }

    [Fact]
    public void ExtractGameSlugs_UsesExplicitFilterFirst()
    {
        var slugs = QueryPreprocessor.ExtractGameSlugs("flipper repair", "Medieval Madness");
        Assert.Contains("medieval-madness", slugs);
    }

    [Fact]
    public void ExtractGameSlugs_FindsMultipleGames()
    {
        var slugs = QueryPreprocessor.ExtractGameSlugs(
            "Compare Medieval Madness versus Attack From Mars", null);
        Assert.Contains("medieval-madness", slugs);
        Assert.Contains("attack-from-mars", slugs);
    }

    [Fact]
    public void ExtractGameSlugs_ReturnsEmptyForUnknownGames()
    {
        var slugs = QueryPreprocessor.ExtractGameSlugs("general pinball question", null);
        Assert.Empty(slugs);
    }

    [Fact]
    public void ExpandQuery_AddsRepairTermsForRepairIntent()
    {
        var expanded = QueryPreprocessor.ExpandQuery("fix flipper", QueryIntent.Repair);
        Assert.Contains("repair", expanded);
        Assert.Contains("troubleshooting", expanded);
        Assert.Contains("maintenance", expanded);
    }

    [Fact]
    public void ExpandQuery_AddsSynonymsForPinballTerms()
    {
        var expanded = QueryPreprocessor.ExpandQuery("fix the flipper", QueryIntent.Repair);
        Assert.Contains("flipper bat", expanded);
        Assert.Contains("flipper assembly", expanded);
    }

    [Fact]
    public void ExpandQuery_KeepsOriginalQueryInResult()
    {
        var expanded = QueryPreprocessor.ExpandQuery("my original question", QueryIntent.General);
        Assert.StartsWith("my original question", expanded);
    }

    [Fact]
    public void Process_ReturnsCompletePreprocessedQuery()
    {
        var result = _preprocessor.Process("How do I fix the left flipper on Medieval Madness?", null);

        Assert.Equal("How do I fix the left flipper on Medieval Madness?", result.OriginalQuery);
        Assert.Equal(QueryIntent.Repair, result.Intent);
        Assert.Contains("medieval-madness", result.GameSlugs);
        Assert.Contains("repair", result.ExpandedQuery);
        Assert.NotEmpty(result.Filters);
    }

    [Fact]
    public void Process_WithGameFilter_IncludesFilterSlug()
    {
        var result = _preprocessor.Process("flipper repair", "Twilight Zone");
        Assert.Contains("twilight-zone", result.GameSlugs);
    }
}
