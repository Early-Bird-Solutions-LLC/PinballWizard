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

    public ConcurrentBag<DeleteCall> DeleteCalls { get; } = [];

    // Number of chunks the fake reports deleted per DeleteByDocumentAndMachineAsync
    // call. Tests that assert delete-prior behavior set this; default 0.
    public int DeleteResult { get; set; }

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

    public Task<int> DeleteByDocumentAndMachineAsync(
        string documentId, string machineId, CancellationToken cancellationToken)
    {
        DeleteCalls.Add(new DeleteCall(documentId, machineId));
        return Task.FromResult(DeleteResult);
    }

    internal sealed record UpsertCall(
        string DocumentId, string MachineId, int ChunkCount, string? Edition, string? EditionScope);

    internal sealed record DeleteCall(string DocumentId, string MachineId);
}

internal sealed class InMemoryIndexState : IIndexState
{
    // Keyed on (documentId, machineId) per the Phase 3 re-attribution fix —
    // one document fanned out to two machines occupies two independent rows.
    private readonly ConcurrentDictionary<(string DocumentId, string MachineId), StateRow> _rows = new();

    public IReadOnlyDictionary<(string DocumentId, string MachineId), StateRow> Snapshot => _rows;

    public Task<string?> GetLastIndexedHashAsync(
        string documentId, string machineId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue((documentId, machineId), out var row) ? row.Hash : null);

    public Task RecordIndexedAsync(
        string documentId,
        string machineId,
        string contentHash,
        int chunkCount,
        int failureCount,
        CancellationToken cancellationToken)
    {
        _rows[(documentId, machineId)] = new StateRow(contentHash, chunkCount, failureCount, SkipReason: null);
        return Task.CompletedTask;
    }

    public Task RecordSkippedAsync(
        string documentId,
        string machineId,
        IngestionOutcome skipOutcome,
        CancellationToken cancellationToken)
    {
        _rows[(documentId, machineId)] = new StateRow(
            Hash: string.Empty,
            ChunkCount: 0,
            FailureCount: 0,
            SkipReason: skipOutcome.ToString());
        return Task.CompletedTask;
    }

    public void SeedExistingHash(string documentId, string machineId, string hash) =>
        _rows[(documentId, machineId)] = new StateRow(hash, ChunkCount: 0, FailureCount: 0, SkipReason: null);

    internal sealed record StateRow(string Hash, int ChunkCount, int FailureCount, string? SkipReason);
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
