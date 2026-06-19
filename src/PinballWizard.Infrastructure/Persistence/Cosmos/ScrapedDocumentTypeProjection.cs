using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Read-side projection over the `scraped_documents` container for the
// catalog-stats doc-type count (the --rebuild-catalog-stats backstop and the
// change-feed projection consumer). Both only need document_type and
// machine_title to build per-machine type-count rollups.
//
// Why a projection instead of reading the full ScrapedDocumentRecord:
// ScrapedDocumentRecord is the WRITE model and carries `required` invariants
// (e.g. edition_scope, added in #318) that documents written before those
// fields existed do not satisfy. Deserializing historical documents into the
// write model throws (missing required property) — which crash-looped the
// catalog-stats BackgroundService and broke --rebuild-catalog-stats on live
// data (2026-06-19 incident). Counting doc-types must never depend on a
// document satisfying the current write-model schema, so this projection
// deliberately declares NO required fields: a missing value degrades to a
// sensible default (empty string), and the count simply skips blanks.
//
// Pair the type with a projecting query — `SELECT c.document_type,
// c.machine_title FROM c` — so only these fields cross the wire.
internal sealed class ScrapedDocumentTypeProjection : IEntity
{
    // IEntity members. The catalog-stats count addresses documents by the
    // partition it already holds (machine_id) and never round-trips this
    // projection back to Cosmos, so these are not part of the projecting
    // query and default to empty. They exist only to satisfy the
    // CosmosRepository<T> : IEntity constraint.
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("machine_id")]
    public string PartitionKey { get; init; } = string.Empty;

    [JsonPropertyName("document_type")]
    public string DocumentType { get; init; } = string.Empty;

    [JsonPropertyName("machine_title")]
    public string MachineTitle { get; init; } = string.Empty;
}
