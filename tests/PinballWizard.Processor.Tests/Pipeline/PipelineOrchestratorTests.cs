using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Domain.Abstractions;
using PinballWizard.Processor.Chunking;
using PinballWizard.Processor.Indexing;
using PinballWizard.Processor.Pipeline;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Xunit;

namespace PinballWizard.Processor.Tests.Pipeline;

public class PipelineOrchestratorTests
{
    private readonly IOptions<ProcessorSettings> _settings = Options.Create(new ProcessorSettings
    {
        ChunkTokenSize = 512,
        ChunkOverlap = 128
    });

    private PipelineOrchestrator CreateOrchestrator()
    {
        var extractors = Array.Empty<IContentExtractor>();
        var slidingWindow = new SlidingWindowChunker(_settings);
        var sectionAware = new SectionAwareChunker();
        var wholeDoc = new WholeDocumentChunker();
        var searchClient = Substitute.For<SearchClient>();
        var publisher = new IndexBatchPublisher(searchClient, _settings, NullLogger<IndexBatchPublisher>.Instance);
        var blobServiceClient = Substitute.For<BlobServiceClient>();

        return new PipelineOrchestrator(
            extractors,
            slidingWindow,
            sectionAware,
            wholeDoc,
            publisher,
            blobServiceClient,
            NullLogger<PipelineOrchestrator>.Instance);
    }

    [Fact]
    public void SelectChunker_ShortDocument_ReturnsWholeDocumentChunker()
    {
        var orchestrator = CreateOrchestrator();
        var result = new ExtractionResult
        {
            Text = "This is a short document.",
            Sections = [new TextSection { Content = "Short content" }]
        };

        var chunker = orchestrator.SelectChunker(result, "text/plain", ".txt");

        Assert.Equal("WholeDocumentChunker", chunker.Name);
    }

    [Fact]
    public void SelectChunker_DocumentWithSections_ReturnsSectionAwareChunker()
    {
        var orchestrator = CreateOrchestrator();

        var longText = string.Join(" ", Enumerable.Repeat("This is a test sentence with enough words to contribute to the token count.", 200));
        var result = new ExtractionResult
        {
            Text = longText,
            Sections =
            [
                new TextSection { Content = "Introduction", Heading = "Chapter 1", Level = 1 },
                new TextSection { Content = longText, Heading = "Chapter 2", Level = 1 }
            ]
        };

        var chunker = orchestrator.SelectChunker(result, "application/pdf", ".pdf");

        Assert.Equal("SectionAwareChunker", chunker.Name);
    }

    [Fact]
    public void SelectChunker_LongDocumentNoSections_ReturnsSlidingWindowChunker()
    {
        var orchestrator = CreateOrchestrator();

        var longText = string.Join(" ", Enumerable.Repeat("This is a test sentence with enough words to contribute to the token count.", 200));
        var result = new ExtractionResult
        {
            Text = longText,
            Sections = [new TextSection { Content = longText }]
        };

        var chunker = orchestrator.SelectChunker(result, "application/pdf", ".pdf");

        Assert.Equal("SlidingWindowChunker", chunker.Name);
    }
}
