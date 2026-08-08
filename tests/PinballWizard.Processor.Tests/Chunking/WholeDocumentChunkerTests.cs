using PinballWizard.Domain.Abstractions;
using PinballWizard.Processor.Chunking;
using Xunit;

namespace PinballWizard.Processor.Tests.Chunking;

public class WholeDocumentChunkerTests
{
    private readonly WholeDocumentChunker _sut = new();

    [Fact]
    public void Name_ReturnsWholeDocumentChunker()
    {
        Assert.Equal("WholeDocumentChunker", _sut.Name);
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsEmpty()
    {
        var result = new ExtractionResult { Text = "", Sections = [] };
        var chunks = _sut.Chunk(result);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_ShortDocument_ReturnsSingleChunk()
    {
        var text = "This is a complete rulesheet for a pinball machine.";
        var result = new ExtractionResult
        {
            Text = text,
            Sections = [new TextSection { Content = text, Heading = "Rulesheet", Level = 1 }]
        };

        var chunks = _sut.Chunk(result);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Content);
        Assert.Equal("Rulesheet", chunks[0].SectionPath);
        Assert.True(chunks[0].TokenCount > 0);
    }

    [Fact]
    public void Chunk_DocumentUnder2048Tokens_ReturnsSingleChunk()
    {
        var paragraphs = Enumerable.Range(1, 10)
            .Select(i => $"Paragraph {i} with some content about pinball machines and repair guides.")
            .ToList();
        var text = string.Join("\n\n", paragraphs);

        var result = new ExtractionResult
        {
            Text = text,
            Sections = [new TextSection { Content = text }]
        };

        var chunks = _sut.Chunk(result);

        Assert.Single(chunks);
    }

    [Fact]
    public void Chunk_VeryLongDocument_SplitsIntoParagraphBoundaries()
    {
        // Generate text that exceeds 2048 tokens
        var paragraphs = Enumerable.Range(1, 200)
            .Select(i => $"This is paragraph number {i}. It contains several sentences about pinball machine maintenance and repair. Each paragraph adds significant token count to the overall document.")
            .ToList();
        var text = string.Join("\n\n", paragraphs);

        var result = new ExtractionResult
        {
            Text = text,
            Sections = [new TextSection { Content = text }]
        };

        var chunks = _sut.Chunk(result);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Chunk_ZeroOverlap_ChunksDontOverlap()
    {
        var paragraphs = Enumerable.Range(1, 200)
            .Select(i => $"Unique paragraph number {i} with some distinct content for testing overlap behavior in chunking.")
            .ToList();
        var text = string.Join("\n\n", paragraphs);

        var result = new ExtractionResult
        {
            Text = text,
            Sections = [new TextSection { Content = text }]
        };

        var chunks = _sut.Chunk(result);

        if (chunks.Count > 1)
        {
            // First chunk's last sentence should not appear in second chunk's start
            var firstEnd = chunks[0].Content[^20..];
            Assert.DoesNotContain(firstEnd, chunks[1].Content[..Math.Min(50, chunks[1].Content.Length)]);
        }
    }
}
