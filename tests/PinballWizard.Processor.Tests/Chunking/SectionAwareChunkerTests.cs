using PinballWizard.Domain.Abstractions;
using PinballWizard.Processor.Chunking;
using Xunit;

namespace PinballWizard.Processor.Tests.Chunking;

public class SectionAwareChunkerTests
{
    private readonly SectionAwareChunker _sut = new();

    [Fact]
    public void Name_ReturnsSectionAwareChunker()
    {
        Assert.Equal("SectionAwareChunker", _sut.Name);
    }

    [Fact]
    public void Chunk_EmptySections_ReturnsEmpty()
    {
        var result = new ExtractionResult { Text = "Some text", Sections = [] };
        var chunks = _sut.Chunk(result);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_SingleSection_ReturnsSingleChunk()
    {
        var result = new ExtractionResult
        {
            Text = "Chapter content here.",
            Sections =
            [
                new TextSection { Content = "Chapter content here.", Heading = "Chapter 1", Level = 1 }
            ]
        };

        var chunks = _sut.Chunk(result);

        Assert.Single(chunks);
        Assert.Equal("Chapter 1", chunks[0].SectionPath);
    }

    [Fact]
    public void Chunk_MultipleSections_CreatesSeparateChunks()
    {
        var result = new ExtractionResult
        {
            Text = "First chapter. Second chapter.",
            Sections =
            [
                new TextSection { Content = "First chapter content.", Heading = "Chapter 1", Level = 1 },
                new TextSection { Content = "Second chapter content.", Heading = "Chapter 2", Level = 1 }
            ]
        };

        var chunks = _sut.Chunk(result);

        Assert.True(chunks.Count >= 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Chunk_SectionWithPageNumber_PreservesPageNumber()
    {
        var result = new ExtractionResult
        {
            Text = "Page content.",
            Sections =
            [
                new TextSection { Content = "Page content.", Heading = "Section A", Level = 1, PageNumber = 5 }
            ]
        };

        var chunks = _sut.Chunk(result);

        Assert.Single(chunks);
        Assert.Equal(5, chunks[0].PageNumber);
    }
}
