using System.Text.Json.Serialization;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// STJ-serializable write-side projection of the AI Search index row
// defined by ADR-0021 § Schema. Symmetric counterpart to the
// retriever's `RetrievedChunkDocument` — that one omits
// `content_embedding` (read side never needs the 3072-d vector); this
// one includes it (write side must populate it).
//
// `Azure.Search.Documents` serializes via STJ and matches by JSON
// property name; each property carries `[JsonPropertyName]` mapping
// to the snake_case index field. Property names in C# stay PascalCase
// to match the project's analyzer conventions (parallel with
// `RetrievedChunkDocument`).
internal sealed class IndexedChunkDocument
{
    [JsonPropertyName(AiSearchIndexFields.ChunkId)]
    public string ChunkId { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.MachineId)]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.MachineTitle)]
    public string MachineTitle { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.Manufacturer)]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.DocumentId)]
    public string DocumentId { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.DocumentUrl)]
    public string DocumentUrl { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.DocumentType)]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.PageStart)]
    public int PageStart { get; set; }

    [JsonPropertyName(AiSearchIndexFields.PageEnd)]
    public int PageEnd { get; set; }

    [JsonPropertyName(AiSearchIndexFields.SectionHeading)]
    public string SectionHeading { get; set; } = string.Empty;

    [JsonPropertyName(AiSearchIndexFields.Content)]
    public string Content { get; set; } = string.Empty;

    // 3072-d vector per ADR-0020 / ADR-0021. Serialized as a JSON
    // number array; the SDK's STJ converter handles `float[]`
    // round-tripping. Never read back — `RetrievedChunkDocument`
    // omits this field on purpose to skip the bandwidth cost.
    [JsonPropertyName(AiSearchIndexFields.ContentEmbedding)]
    public float[] ContentEmbedding { get; set; } = [];

    // Timeline.LastDownloadedAt from the Phase 1 scraper provenance record.
    // See AiSearchIndexFields.LastScrapedUtc for the semantic rationale
    // (LastDownloadedAt vs LastContentChangedAt). Nullable because
    // existing indexed chunks before PR-C3 carry null; new ingest runs
    // populate it going forward (zero-migration-cost per ADR-0025 § 6).
    [JsonPropertyName(AiSearchIndexFields.LastScrapedUtc)]
    public DateTimeOffset? LastScrapedUtc { get; set; }

    // edition — free-text edition label ("Pro" / "Premium" / "LE") from the
    // scraped_documents provenance record. Nullable: legacy chunks indexed
    // before Task 6 (AB#259) carry null; unresolved documents may have no
    // edition. See AiSearchIndexFields.Edition.
    [JsonPropertyName(AiSearchIndexFields.Edition)]
    public string? Edition { get; set; }

    // edition_scope — structural scope within the franchise
    // (single-edition / edition-subset / franchise-wide). The signal the
    // Wizard uses to decide answer-all vs honest-substitution (R1/R2/R3).
    // Nullable for the same legacy/unresolved reasons as Edition above.
    [JsonPropertyName(AiSearchIndexFields.EditionScope)]
    public string? EditionScope { get; set; }
}
