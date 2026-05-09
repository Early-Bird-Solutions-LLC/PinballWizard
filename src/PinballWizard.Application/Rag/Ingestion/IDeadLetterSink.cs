namespace PinballWizard.Application.Rag.Ingestion;

// Per-document dead-letter abstraction for the W3-2 hosted service.
// Records documents whose ingestion has thrown beyond the per-document
// retry budget (`RagIngestionOptions.MaxFailuresPerDocument`).
//
// The sink is consulted on every change-feed delivery: when a record
// for `documentId` exists with `AttemptCount >= MaxFailuresPerDocument`
// the hosted service short-circuits without invoking the pipeline.
// This stops the Change Feed from re-trying a structurally-poison
// document (malformed PDF, an AI Search schema field that won't pass
// validation, etc.) on every re-delivery while still letting an
// operator clear the dead-letter row to re-queue.
//
// Persistence is per-document (NOT per-attempt-event) — re-deliveries
// upsert the same row with an incremented AttemptCount. This keeps
// the dead-letter container bounded by document cardinality, not by
// failure count, which matters for the curated-subset scale where a
// single bad chunk could otherwise produce a row per Change Feed
// re-delivery cycle.
//
// The default Infrastructure implementation backs this with a Cosmos
// container `rag_dead_letters` keyed on `/document_id`. Tests use an
// in-memory fake.
public interface IDeadLetterSink
{
    // Returns the existing dead-letter record for `documentId` or null
    // if no failures have been recorded. Used by the hosted service
    // BEFORE invoking the pipeline so the pipeline never runs for a
    // document that's already over the retry budget.
    Task<DeadLetterRecord?> GetAsync(
        string documentId,
        CancellationToken cancellationToken);

    // Idempotently upserts the dead-letter row. Callers compute the
    // new AttemptCount themselves (existing.AttemptCount + 1, or 1 if
    // not yet recorded) so the sink stays a pure persistence concern.
    Task UpsertAsync(
        DeadLetterRecord record,
        CancellationToken cancellationToken);
}

// One dead-letter record per document. `AttemptCount` is the cumulative
// retry total since the last operator-clear; `LastAttemptUtc` is the
// most recent attempt's timestamp; `ErrorClass` is the exception type
// name (without namespace, capped at 64 chars by the hosted service to
// keep the container row small) and `ErrorMessage` is the truncated
// message (capped at 1024 chars). `ChangeLsn` is the Cosmos `_lsn` of
// the source-document version that triggered the failure; useful for
// operators reproducing the failure against a specific Cosmos snapshot.
public sealed record DeadLetterRecord(
    string DocumentId,
    int AttemptCount,
    DateTimeOffset LastAttemptUtc,
    string ErrorClass,
    string ErrorMessage,
    string? ChangeLsn);
