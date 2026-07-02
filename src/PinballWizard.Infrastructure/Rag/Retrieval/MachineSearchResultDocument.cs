using System.Text.Json.Serialization;
using PinballWizard.Infrastructure.Rag.Indexing;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// Read-side projection of a machine findability index hit (ADR-0049 phase 2b).
// Symmetric in style to RetrievedChunkDocument (RAG corpus read-side), but
// targeted at the machine index. Fields map to the subset needed to build a
// MachineSearchHit without projecting the full index document — limiting
// Select to these fields keeps response payloads small.
//
// Write-side projection is MachineSearchDocument (which MachineSearchIndexProjector
// uses when indexing). We keep the read and write projections separate so the
// write-side can carry fields that the query-time caller does not need (e.g.
// title_prefix and title_phonetic are write-only — AI Search uses them at query
// time but we never project them back in Select).
internal sealed class MachineSearchResultDocument
{
    [JsonPropertyName(MachineSearchIndexFields.Id)]
    public string Id { get; set; } = string.Empty;

    // Standard-analyzer BM25 / synonym-expansion title — the display title.
    // Authoritative title for the returned hit (Cosmos point-read may be
    // skipped for collision candidates that only need OpdbId + GroupId).
    [JsonPropertyName(MachineSearchIndexFields.Title)]
    public string Title { get; set; } = string.Empty;

    // Human-readable manufacturer display name (e.g. "Stern Pinball").
    [JsonPropertyName(MachineSearchIndexFields.Manufacturer)]
    public string Manufacturer { get; set; } = string.Empty;

    // Partition-key form (lowercase, e.g. "stern"). Required for the Cosmos
    // GetByOpdbIdAsync(opdbId, manufacturer) point-read that fetches the full
    // Machine record (including Editions, EditionLabel, EditionTokens).
    [JsonPropertyName(MachineSearchIndexFields.ManufacturerKey)]
    public string ManufacturerKey { get; set; } = string.Empty;

    // OPDB group ID. Used to collapse same-group hits into the resolved primary
    // (reachable via Siblings) rather than surfacing them as TitleCollisions.
    [JsonPropertyName(MachineSearchIndexFields.GroupId)]
    public string? GroupId { get; set; }

    // Release year. Projected for debug logging only — year is not used in the
    // reroute resolution logic but helps operators diagnose disambiguation.
    [JsonPropertyName(MachineSearchIndexFields.Year)]
    public int? Year { get; set; }
}
