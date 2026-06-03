using PinballWizard.Application.Linking;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

// Write-side abstraction for the `scraped_documents` Cosmos container.
// The read side (Change Feed consumer projection) lives in the Infrastructure
// layer (`RagSourceDocument`) because it is tightly coupled to the Cosmos
// SDK's change-feed deserialization path. This interface covers only the
// upsert path needed by the CLI seeder (`--seed-scraped-documents`) and
// the document linker (`DocumentLinker`).
public interface IScrapedDocumentRepository
{
    // Idempotently upsert a `scraped_documents` record derived from a
    // `DocumentRecord` catalog entry. The repository constructs the
    // Cosmos document shape (including partition key `machine_id`) from
    // the supplied parameters. Returns true when the item was inserted,
    // false when an existing item was overwritten.
    Task UpsertAsync(
        DocumentRecord record,
        string machineId,
        string machineTitle,
        string manufacturer,
        CancellationToken cancellationToken);

    // Linker-side upsert: writes a `scraped_documents` record from a
    // `RawDocumentRecord` after the linker has resolved the machine.
    // The document `Id` is "{raw.DocumentId}_{machineId}" so one raw
    // record can fan-out to multiple machine partitions without collision.
    // `editionScope` records whether the document applies to a single edition,
    // a subset of editions, or the whole franchise — the resolved structural
    // scope from the linker's edition resolver. It is persisted alongside the
    // free-text `edition` label and carried downstream into the chunk pipeline.
    Task UpsertFromRawAsync(
        RawDocumentRecord raw,
        string machineId,
        string machineTitle,
        string manufacturer,
        string? edition,
        EditionScope editionScope,
        CancellationToken cancellationToken);

    // Streams the machine_ids of every existing fan-out row for a document
    // (id = "{documentId}_{machineId}"). Used by the linker to detect — and
    // prune — rows for machines a re-link no longer resolves to, so --relink-all
    // is idempotent and never leaves orphaned fan-out rows.
    IAsyncEnumerable<string> StreamByDocumentIdAsync(string documentId, CancellationToken cancellationToken);

    // Point-deletes the single fan-out row "{documentId}_{machineId}" in the
    // machineId partition. No-op (not an error) if the row is already absent.
    Task DeleteFanOutRowAsync(string documentId, string machineId, CancellationToken cancellationToken);
}
