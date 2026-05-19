using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

// Write-side abstraction for the `scraped_documents` Cosmos container.
// The read side (Change Feed consumer projection) lives in the Infrastructure
// layer (`RagSourceDocument`) because it is tightly coupled to the Cosmos
// SDK's change-feed deserialization path. This interface covers only the
// upsert path needed by the CLI seeder (`--seed-scraped-documents`).
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
}
