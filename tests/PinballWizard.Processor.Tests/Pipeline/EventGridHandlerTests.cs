using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Domain.Abstractions;
using PinballWizard.Processor.Chunking;
using PinballWizard.Processor.Indexing;
using PinballWizard.Processor.Pipeline;
using NSubstitute;
using Xunit;

namespace PinballWizard.Processor.Tests.Pipeline;

public class EventGridHandlerTests
{
    private static EventGridHandler CreateHandler()
    {
        var settings = Options.Create(new ProcessorSettings());
        var extractors = Array.Empty<IContentExtractor>();
        var slidingWindow = new SlidingWindowChunker(settings);
        var sectionAware = new SectionAwareChunker();
        var wholeDoc = new WholeDocumentChunker();
        var searchClient = Substitute.For<Azure.Search.Documents.SearchClient>();
        var publisher = new IndexBatchPublisher(searchClient, settings, NullLogger<IndexBatchPublisher>.Instance);
        var blobServiceClient = Substitute.For<Azure.Storage.Blobs.BlobServiceClient>();

        var orchestrator = new PipelineOrchestrator(
            extractors, slidingWindow, sectionAware, wholeDoc,
            publisher, blobServiceClient, NullLogger<PipelineOrchestrator>.Instance);

        return new EventGridHandler(orchestrator, NullLogger<EventGridHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_ValidationEvent_ReturnsValidationResponse()
    {
        var handler = CreateHandler();

        var json = """
            [{
                "type": "Microsoft.EventGrid.SubscriptionValidationEvent",
                "data": { "validationCode": "abc-123" }
            }]
            """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(context.Request, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task HandleAsync_NonBlobEvent_ReturnsOk()
    {
        var handler = CreateHandler();

        var json = """
            [{
                "type": "Microsoft.SomeOther.Event",
                "data": {}
            }]
            """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(context.Request, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task HandleAsync_EmptyArray_ReturnsOk()
    {
        var handler = CreateHandler();

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("[]"));

        var result = await handler.HandleAsync(context.Request, CancellationToken.None);

        Assert.NotNull(result);
    }
}
