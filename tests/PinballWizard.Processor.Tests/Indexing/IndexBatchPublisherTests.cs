using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Domain.Models;
using PinballWizard.Processor.Indexing;
using Xunit;

namespace PinballWizard.Processor.Tests.Indexing;

public class IndexBatchPublisherTests
{
    [Fact]
    public async Task PublishAsync_EmptyList_DoesNothing()
    {
        var searchClient = Substitute.For<SearchClient>();
        var settings = Options.Create(new ProcessorSettings { IndexBatchSize = 100 });
        var publisher = new IndexBatchPublisher(searchClient, settings, NullLogger<IndexBatchPublisher>.Instance);

        await publisher.PublishAsync([], CancellationToken.None);

        await searchClient.DidNotReceive().IndexDocumentsAsync(
            Arg.Any<IndexDocumentsBatch<SearchChunk>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_SingleBatch_CallsIndexDocumentsOnce()
    {
        var searchClient = Substitute.For<SearchClient>();
        var settings = Options.Create(new ProcessorSettings { IndexBatchSize = 100 });
        var publisher = new IndexBatchPublisher(searchClient, settings, NullLogger<IndexBatchPublisher>.Instance);

        var chunks = CreateChunks(5);

        await publisher.PublishAsync(chunks, CancellationToken.None);

        await searchClient.Received(1).IndexDocumentsAsync(
            Arg.Any<IndexDocumentsBatch<SearchChunk>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_MultipleBatches_CallsIndexDocumentsMultipleTimes()
    {
        var searchClient = Substitute.For<SearchClient>();
        var settings = Options.Create(new ProcessorSettings { IndexBatchSize = 3 });
        var publisher = new IndexBatchPublisher(searchClient, settings, NullLogger<IndexBatchPublisher>.Instance);

        var chunks = CreateChunks(7);

        await publisher.PublishAsync(chunks, CancellationToken.None);

        // 7 chunks / 3 batch size = 3 batches
        await searchClient.Received(3).IndexDocumentsAsync(
            Arg.Any<IndexDocumentsBatch<SearchChunk>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    private static List<SearchChunk> CreateChunks(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new SearchChunk
            {
                ChunkId = $"chunk_{i}",
                Content = $"Content for chunk {i}",
                ParentDocId = "doc_test"
            })
            .ToList();
    }
}
