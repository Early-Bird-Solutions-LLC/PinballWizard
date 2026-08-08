using Microsoft.Extensions.Options;
using PinballWizard.Api.Pipeline;
using PinballWizard.Domain.Models;
using Xunit;

namespace PinballWizard.Api.Tests;

public class ContextAssemblerTests
{
    private static ContextAssembler CreateAssembler(int tokenBudget = 12_000)
    {
        var settings = Options.Create(new ApiSettings { ContextTokenBudget = tokenBudget });
        return new ContextAssembler(settings);
    }

    private static ScoredChunk CreateChunk(string id, string content, double score,
        string parentDocId = "doc1", string? sectionPath = null)
    {
        return new ScoredChunk
        {
            Chunk = new SearchChunk
            {
                ChunkId = id,
                Content = content,
                ParentDocId = parentDocId,
                SourceName = $"Source for {id}",
                SourceUrl = $"https://example.com/{id}",
                DocumentType = DocumentType.Manual,
                PageNumber = 1,
                SectionPath = sectionPath ?? $"section-{id}"
            },
            Score = score
        };
    }

    [Fact]
    public void Assemble_OrdersByRelevanceScore()
    {
        var assembler = CreateAssembler();
        var chunks = new List<ScoredChunk>
        {
            CreateChunk("low", "Low score content", 0.5),
            CreateChunk("high", "High score content", 0.9),
            CreateChunk("mid", "Mid score content", 0.7)
        };

        var result = assembler.Assemble(chunks);

        Assert.Equal(3, result.Blocks.Count);
        Assert.Equal("Source for high", result.Blocks[0].SourceName);
        Assert.Equal("Source for mid", result.Blocks[1].SourceName);
        Assert.Equal("Source for low", result.Blocks[2].SourceName);
    }

    [Fact]
    public void Assemble_RespectsTokenBudget()
    {
        // Very small budget that can only fit ~1 chunk
        var assembler = CreateAssembler(tokenBudget: 20);
        var chunks = new List<ScoredChunk>
        {
            CreateChunk("1", "Short content", 0.9),
            CreateChunk("2", "This is a longer piece of content that has many more tokens", 0.8)
        };

        var result = assembler.Assemble(chunks);

        // Should include at least the first chunk but stop when budget exceeded
        Assert.True(result.Blocks.Count <= 2);
        Assert.True(result.TotalTokens <= 20 || result.Blocks.Count == 1);
    }

    [Fact]
    public void Assemble_DeduplicatesSameParentDocAndSection()
    {
        var assembler = CreateAssembler();
        var chunks = new List<ScoredChunk>
        {
            CreateChunk("1", "First chunk", 0.9, parentDocId: "doc1", sectionPath: "intro"),
            CreateChunk("2", "Duplicate section", 0.8, parentDocId: "doc1", sectionPath: "intro"),
            CreateChunk("3", "Different section", 0.7, parentDocId: "doc1", sectionPath: "details")
        };

        var result = assembler.Assemble(chunks);

        // Should deduplicate: only 2 unique (doc1|intro) and (doc1|details)
        Assert.Equal(2, result.Blocks.Count);
    }

    [Fact]
    public void Assemble_FormatsContextBlocksWithSourceMetadata()
    {
        var assembler = CreateAssembler();
        var chunks = new List<ScoredChunk>
        {
            CreateChunk("1", "Test content here", 0.9)
        };

        var result = assembler.Assemble(chunks);

        Assert.Contains("[1]", result.FormattedContext);
        Assert.Contains("Source for 1", result.FormattedContext);
        Assert.Contains("page: 1", result.FormattedContext);
        Assert.Contains("Test content here", result.FormattedContext);
    }

    [Fact]
    public void Assemble_AssignsSequentialIndices()
    {
        var assembler = CreateAssembler();
        var chunks = new List<ScoredChunk>
        {
            CreateChunk("a", "Content A", 0.9, sectionPath: "s1"),
            CreateChunk("b", "Content B", 0.8, sectionPath: "s2"),
            CreateChunk("c", "Content C", 0.7, sectionPath: "s3")
        };

        var result = assembler.Assemble(chunks);

        Assert.Equal(1, result.Blocks[0].Index);
        Assert.Equal(2, result.Blocks[1].Index);
        Assert.Equal(3, result.Blocks[2].Index);
    }

    [Fact]
    public void Assemble_EmptyInput_ReturnsEmptyResult()
    {
        var assembler = CreateAssembler();
        var result = assembler.Assemble([]);

        Assert.Empty(result.Blocks);
        Assert.Equal("", result.FormattedContext);
        Assert.Equal(0, result.TotalTokens);
    }

    [Fact]
    public void CountTokens_ReturnsPositiveValueForNonEmptyText()
    {
        var count = ContextAssembler.CountTokens("Hello, this is a test sentence.");
        Assert.True(count > 0);
    }

    [Fact]
    public void Deduplicate_KeepsHigherScoredChunk()
    {
        var chunks = new List<ScoredChunk>
        {
            CreateChunk("1", "Lower score", 0.5, parentDocId: "doc1", sectionPath: "intro"),
            CreateChunk("2", "Higher score", 0.9, parentDocId: "doc1", sectionPath: "intro")
        };

        var result = ContextAssembler.Deduplicate(chunks);

        Assert.Single(result);
        Assert.Equal(0.9, result[0].Score);
    }
}
