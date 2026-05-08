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
}
