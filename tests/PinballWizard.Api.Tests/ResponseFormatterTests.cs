using PinballWizard.Api.Pipeline;
using PinballWizard.Domain.Models;
using Xunit;

namespace PinballWizard.Api.Tests;

public class ResponseFormatterTests
{
    private readonly ResponseFormatter _formatter = new();

    private static List<ContextBlock> CreateBlocks(int count)
    {
        return Enumerable.Range(1, count).Select(i => new ContextBlock
        {
            Index = i,
            Content = $"Content for block {i}",
            SourceName = $"Source {i}",
            SourceUrl = $"https://example.com/{i}",
            DocumentType = "Manual",
            GameTitle = "Test Game",
            SectionPath = $"section-{i}",
            Score = 1.0 - (i * 0.1),
            PageNumber = i
        }).ToList();
    }

    [Fact]
    public void Format_KeepsValidCitations()
    {
        var blocks = CreateBlocks(3);
        var text = "According to [1], the flipper needs repair. See also [2] for parts.";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Contains("[1]", result.Answer);
        Assert.Contains("[2]", result.Answer);
        Assert.Equal(2, result.Sources.Count);
    }

    [Fact]
    public void Format_RemovesInvalidCitations()
    {
        var blocks = CreateBlocks(2);
        var text = "Valid [1] and invalid [5] citation.";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Contains("[1]", result.Answer);
        Assert.DoesNotContain("[5]", result.Answer);
    }

    [Fact]
    public void Format_BuildsCorrectSourceCitations()
    {
        var blocks = CreateBlocks(3);
        var text = "Info from [1] and [3].";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Equal(2, result.Sources.Count);
        var source1 = result.Sources.First(s => s.Index == 1);
        Assert.Equal("Source 1", source1.Title);
        Assert.Equal("https://example.com/1", source1.Url);
        Assert.Equal("Manual", source1.DocumentType);
        Assert.Equal("Test Game", source1.GameTitle);
    }

    [Fact]
    public void Format_SourcesOrderedByIndex()
    {
        var blocks = CreateBlocks(5);
        var text = "See [3], [1], and [5].";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.Equal(3, result.Sources[1].Index);
        Assert.Equal(5, result.Sources[2].Index);
    }

    [Fact]
    public void Format_SetsConversationId()
    {
        var result = _formatter.Format("my-conv-id", "Simple text", []);
        Assert.Equal("my-conv-id", result.ConversationId);
    }

    [Fact]
    public void Format_TrimsWhitespace()
    {
        var result = _formatter.Format("conv1", "  answer with whitespace  ", []);
        Assert.Equal("answer with whitespace", result.Answer);
    }

    [Fact]
    public void Format_NoCitations_ReturnsEmptySources()
    {
        var blocks = CreateBlocks(3);
        var text = "Answer with no citations at all.";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Empty(result.Sources);
    }

    [Fact]
    public void Format_AllCitationsInvalid_ReturnsEmptySources()
    {
        var blocks = CreateBlocks(2);
        var text = "Invalid [10] and [20] citations.";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Empty(result.Sources);
        Assert.DoesNotContain("[10]", result.Answer);
        Assert.DoesNotContain("[20]", result.Answer);
    }

    [Fact]
    public void Format_DuplicateCitations_IncludesSourceOnce()
    {
        var blocks = CreateBlocks(2);
        var text = "According to [1], and also [1] again.";

        var result = _formatter.Format("conv1", text, blocks);

        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
    }
}
