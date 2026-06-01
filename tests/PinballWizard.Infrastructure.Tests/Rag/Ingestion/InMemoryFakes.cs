using System.Collections.Concurrent;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

// Test-only in-memory fakes for the W3-2 ingestion stack.
//
// These intentionally avoid NSubstitute for the hot-path collaborators
// (indexer / index state / dead-letter sink) so the integration tests
// can exercise concurrency-tolerant assertions against `ConcurrentBag`
// /  `ConcurrentDictionary` storage rather than working around
// NSubstitute's call-record race conditions in parallel xUnit runs.

internal sealed class InMemoryRagIndexer : IRagIndexer
{
    public ConcurrentBag<UpsertCall> Calls { get; } = [];

    // Default = success; tests that need a transport-level failure swap
    // this for an exception-throwing impl.
    public Func<ChunkRequest, IReadOnlyList<Chunk>, IndexUpsertResult> ResultFactory { get; set; } =
        (_, chunks) => new IndexUpsertResult(chunks.Count, []);

    public Task<IndexUpsertResult> UpsertAsync(
        ChunkRequest request,
        IReadOnlyList<Chunk> chunks,
        RagIndexerOptions options,
        CancellationToken cancellationToken)
    {
        Calls.Add(new UpsertCall(
            request.DocumentId, request.MachineId, chunks.Count, request.Edition, request.EditionScope));
        return Task.FromResult(ResultFactory(request, chunks));
    }

    internal sealed record UpsertCall(
        string DocumentId, string MachineId, int ChunkCount, string? Edition, string? EditionScope);
}

internal sealed class InMemoryIndexState : IIndexState
{
    private readonly ConcurrentDictionary<string, StateRow> _rows = new();

    public IReadOnlyDictionary<string, StateRow> Snapshot => _rows;

    public Task<string?> GetLastIndexedHashAsync(string documentId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(documentId, out var row) ? row.Hash : null);

    public Task RecordIndexedAsync(
        string documentId,
        string contentHash,
        int chunkCount,
        int failureCount,
        CancellationToken cancellationToken)
    {
        _rows[documentId] = new StateRow(contentHash, chunkCount, failureCount);
        return Task.CompletedTask;
    }

    public void SeedExistingHash(string documentId, string hash) =>
        _rows[documentId] = new StateRow(hash, ChunkCount: 0, FailureCount: 0);

    internal sealed record StateRow(string Hash, int ChunkCount, int FailureCount);
}

internal sealed class InMemoryDeadLetterSink : IDeadLetterSink
{
    private readonly ConcurrentDictionary<string, DeadLetterRecord> _rows = new();

    public IReadOnlyDictionary<string, DeadLetterRecord> Snapshot => _rows;

    public Task<DeadLetterRecord?> GetAsync(string documentId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(documentId, out var record) ? record : null);

    public Task UpsertAsync(DeadLetterRecord record, CancellationToken cancellationToken)
    {
        _rows[record.DocumentId] = record;
        return Task.CompletedTask;
    }

    public void SeedExisting(DeadLetterRecord record) =>
        _rows[record.DocumentId] = record;
}

internal sealed class InMemoryDocumentBytesSource : IDocumentBytesSource
{
    public ConcurrentBag<string> Calls { get; } = [];

    public Func<string, byte[]>? PayloadFactory { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "cs/local-not-disposed",
        Justification = "MemoryStream ownership transfers to the caller via the returned Task<Stream>; the caller is responsible for disposal.")]
    public Task<Stream> OpenAsync(string documentUrl, CancellationToken cancellationToken)
    {
        Calls.Add(documentUrl);
        var bytes = PayloadFactory?.Invoke(documentUrl) ?? "PDF-PLACEHOLDER"u8.ToArray();
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
