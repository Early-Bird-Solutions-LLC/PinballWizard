using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// STJ-deserializable projection of the AI Search index row defined by
// ADR-0021 § Schema. The retriever (W3-3) reads only the read-side
// fields it needs to render citations and feed the orchestrator —
// `content_embedding` is intentionally NOT projected (returning
// 3072-dimensional vectors per result is bandwidth + memory waste).
//
// `Azure.Search.Documents` deserializes via STJ and matches by JSON
// property name, so each property carries `[JsonPropertyName]` to the
// snake_case index field. Property names in C# stay PascalCase to
// match the project's analyzer conventions.
internal sealed class RetrievedChunkDocument
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

    // Timeline.LastDownloadedAt projected from the index. Nullable
    // because chunks indexed before PR-C3 carry null; the retriever
    // surfaces null gracefully and the citation extractor propagates
    // null to Citation.LastScrapedUtc so the frontend freshness badge
    // is conditionally rendered.
    [JsonPropertyName(AiSearchIndexFields.LastScrapedUtc)]
    public DateTimeOffset? LastScrapedUtc { get; set; }

    // edition — free-text edition label ("Pro" / "Premium" / "LE")
    // carried from the scraper provenance record (Task 6, AB#259).
    // Projected back so the Wizard can attribute per-edition answers
    // (R2). Nullable: chunks indexed before Task 6 carry null, and
    // unresolved documents may have no edition. The index field is
    // retrievable (String fields are retrievable unless IsHidden, which
    // AiSearchIndexSchema does not set), so AI Search returns it.
    [JsonPropertyName(AiSearchIndexFields.Edition)]
    public string? Edition { get; set; }

    // edition_scope — structural scope within the franchise
    // (single-edition / edition-subset / franchise-wide). The
    // machine-readable signal the Wizard inspects to decide R1 (answer
    // once, all editions) vs R2 (answer per edition) vs R3 (honest
    // substitution). Nullable for the same legacy/unresolved reasons.
    [JsonPropertyName(AiSearchIndexFields.EditionScope)]
    public string? EditionScope { get; set; }
}
