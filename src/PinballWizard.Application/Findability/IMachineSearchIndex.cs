namespace PinballWizard.Application.Findability;

// Abstraction over the AI Search machine findability index (ADR-0049 phase 2b).
// Defined in Application so the grounding tool and eval seam depend on the
// interface, not the Azure SDK. Infrastructure provides AiSearchMachineIndex;
// a null-object is resolved when AI Search is not configured.
//
// Callers receive hits ranked by the "machine-content-intrinsic" scoring
// profile — completeness (magnitude) + freshness (freshness) over the BM25
// text match. The ranking is better than Cosmos CONTAINS for all five
// findability categories: synonyms/abbreviations, partial/subtitle matches,
// prefix/typeahead, phonetic typos, and content-quality ordering.
public interface IMachineSearchIndex
{
    // Returns OPDB IDs ranked by descending relevance (highest score first).
    // `top` bounds the result set; callers that only need one result pass top=1.
    // When `manufacturerKey` is non-null/non-whitespace, results are restricted to
    // that manufacturer partition (server-side filter) — used by ingestion-time
    // resolution that already knows the manufacturer. Null = unscoped (the
    // getMachineByTitle default). An empty list is a valid honest-miss answer —
    // callers must not fabricate.
    Task<IReadOnlyList<MachineSearchHit>> SearchAsync(
        string query,
        int top,
        string? manufacturerKey,
        CancellationToken cancellationToken);
}

// A single ranked hit from the machine findability index. Contains the fields
// needed to (a) point-read the full Machine record from Cosmos for the primary
// result and (b) detect same-group duplicates for TitleCollision deduplication
// without extra round-trips.
public sealed record MachineSearchHit(
    // OPDB canonical machine ID (the index key). Used for Cosmos point-read.
    string OpdbId,

    // Display title from the index — used for debug logging; authoritative
    // title comes from the Cosmos point-read.
    string Title,

    // Human-readable manufacturer display name (e.g. "Stern Pinball").
    string ManufacturerDisplayName,

    // Partition-key form of manufacturer (lowercase, e.g. "stern"). Required
    // for the Cosmos GetByOpdbIdAsync(opdbId, manufacturer) point-read.
    string ManufacturerKey,

    // OPDB group ID (leading segment, e.g. "GweeP"). Used to collapse
    // same-group hits into the primary + Siblings rather than surfacing
    // them as TitleCollisions. Null for ungrouped machines.
    string? GroupId,

    // Release year from the index. Optional — some OPDB entries lack year.
    int? Year,

    // AI Search composite score (BM25 × scoring-profile boost). Used for
    // debug logging and eval; resolution order is determined by index rank.
    double Score);
