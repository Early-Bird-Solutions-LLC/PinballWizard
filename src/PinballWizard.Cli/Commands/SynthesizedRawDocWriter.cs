using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Cli.Commands;

internal static class SynthesizedRawDocWriter
{
    // Upserts a synthesized DocumentRecord to scraped_documents_raw and immediately
    // sets its LinkStatus to PlatformGeneric so the linker skips it. Returns true on
    // success (or when rawDocRepo is null — no Cosmos configured). Returns false and
    // logs a warning on any transient error so callers can meter the failure without
    // aborting the overall sync run (degrade-visibly, invariant #17).
    internal static async Task<bool> TryPersistAsync(
        IRawDocumentRepository? rawDocRepo, DocumentRecord record, CancellationToken ct)
    {
        if (rawDocRepo is null) return true; // no doc store configured — not a write failure
        try
        {
            await rawDocRepo.UpsertRawAsync(record, ct);
            await rawDocRepo.UpdateLinkStatusAsync(record.DocumentId, LinkStatus.PlatformGeneric, "synthesized", null, null, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  Warning: raw-doc store write failed for {record.DocumentId}: {ex.Message}");
            return false;
        }
    }
}
