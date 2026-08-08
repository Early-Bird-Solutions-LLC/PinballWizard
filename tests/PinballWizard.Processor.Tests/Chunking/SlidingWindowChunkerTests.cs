using Microsoft.Extensions.Options;
using PinballWizard.Domain.Abstractions;
using PinballWizard.Processor.Chunking;
using Xunit;

namespace PinballWizard.Processor.Tests.Chunking;

public class SlidingWindowChunkerTests
{
    private readonly SlidingWindowChunker _sut;

    public SlidingWindowChunkerTests()
    {
        var settings = Options.Create(new ProcessorSettings
        {
            ChunkTokenSize = 50,
            ChunkOverlap = 10
        });
        _sut = new SlidingWindowChunker(settings);
    }

    [Fact]
    public void Name_ReturnsSlidingWindowChunker()
    {
        Assert.Equal("SlidingWindowChunker", _sut.Name);
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsEmpty()
    {
        var result = new ExtractionResult { Text = "", Sections = [] };
        var chunks = _sut.Chunk(result);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var result = new ExtractionResult
        {
            Text = "This is a short sentence.",
            Sections = [new TextSection { Content = "This is a short sentence." }]
        };

        var chunks = _sut.Chunk(result);

        Assert.Single(chunks);
        Assert.Contains("short sentence", chunks[0].Content);
        Assert.True(chunks[0].TokenCount > 0);
    }

    [Fact]
    public void Chunk_LongText_CreatesMultipleChunks()
    {
        // Generate text long enough to require multiple chunks at 50-token limit
        var sentences = Enumerable.Range(1, 50)
            .Select(i => $"Sentence number {i} contains some words for testing purposes.")
            .ToList();
        var text = string.Join(" ", sentences);

        var result = new ExtractionResult
        {
            Text = text,
            Sections = [new TextSection { Content = text }]
        };

        var chunks = _sut.Chunk(result);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");
    }

    [Fact]
    public void Chunk_AllChunks_HavePositiveTokenCount()
    {
        var sentences = Enumerable.Range(1, 30)
            .Select(i => $"This is test sentence number {i} with some content.")
            .ToList();
        var text = string.Join(" ", sentences);

        var result = new ExtractionResult
        {
            Text = text,
            Sections = [new TextSection { Content = text }]
        };

        var chunks = _sut.Chunk(result);

        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount > 0));
    }
}
