namespace PinballWizard.Application.Findability;

// Abstracts the Infrastructure projection step: streams all Machine records
// from IMachineRepository, maps each to a machine-index document, and bulk-
// upserts into the AI Search machine index. Defined in Application so the
// CLI and future callers (scheduled jobs, admin API) depend on the interface,
// not the Azure SDK implementation.
//
// ADR-0049 phase 2a: index schema + projection. Phase 2b will reroute
// getMachineByTitle queries to this index; phase 3 adds real-time
// Change-Feed maintenance.
public interface IMachineSearchIndexProjector
{
    // Project all machines into the AI Search machine index. Idempotent:
    // existing documents are merged/overwritten (AI Search upsert semantics).
    // Returns the count of documents projected.
    Task<MachineIndexProjectionResult> ProjectAllAsync(CancellationToken cancellationToken);
}

public sealed record MachineIndexProjectionResult(int Projected, int Failed, TimeSpan Duration);
