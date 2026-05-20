namespace PinballWizard.Application.Rag.Ingestion;

// One-shot backfill service that drives all existing Cosmos
// `scraped_documents` through the RAG ingestion pipeline without
// relying on the Change Feed Processor.
//
// Background: the Cosmos Change Feed v3 Processor with lease-store
// checkpointing only sees changes that are still buffered in the
// server-side feed. Documents written before the processor first
// ran are unreachable via `WithStartTime` once a lease checkpoint
// exists — `WithStartTime` is only honored on the FIRST lease
// initialization, and the initial continuation token the SDK writes
// resolves to the current tail (not the configured start time) when
// the feed has no retained history for that period.
//
// This service iterates the container's change feed directly using
// `ChangeFeedStartFrom.Beginning()` (raw stream iterator, no leases)
// and hands each document to the same `ICosmosChangeFeedHandler<T>`
// used by the hosted service. Idempotent: the pipeline's
// `IIndexState`-based hash short-circuit ensures already-indexed
// documents are skipped on re-runs.
//
// Intended usage: `dotnet run -- --run-rag-backfill` on first deploy
// after the RAG index is provisioned. After backfill completes the
// Change Feed Processor handles ongoing writes.
public interface IRagBackfillService
{
    Task<RagBackfillResult> RunAsync(CancellationToken cancellationToken);
}

public sealed record RagBackfillResult(
    int Processed,
    int Indexed,
    int Skipped,
    int Failed,
    TimeSpan Duration);
