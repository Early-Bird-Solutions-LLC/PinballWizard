using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// STJ-serializable write-side projection of the machine search index row
// defined by ADR-0049. Symmetric in style to IndexedChunkDocument (which
// is the RAG corpus projection). Azure.Search.Documents serializes via STJ
// and matches fields by JSON property name; each property carries
// [JsonPropertyName] mapping to the snake_case index field defined in
// MachineSearchIndexSchema.
//
// The three title variants (title / title_prefix / title_phonetic) are
// WRITE-ONLY during indexing — we always write all three from the same
// Machine.Title value. At query time (Phase 2b) the caller targets specific
// fields for different query intents (exact/BM25 → title, prefix/typeahead
// → title_prefix, sound-alike → title_phonetic).
internal sealed class MachineSearchDocument
{
    [JsonPropertyName(MachineSearchIndexFields.Id)]
    public string Id { get; set; } = string.Empty;

    // Standard analyzer — BM25 / exact keyword search, semantic ranking
    [JsonPropertyName(MachineSearchIndexFields.Title)]
    public string Title { get; set; } = string.Empty;

    // Edge-n-gram analyzer — prefix / typeahead matching
    [JsonPropertyName(MachineSearchIndexFields.TitlePrefix)]
    public string TitlePrefix { get; set; } = string.Empty;

    // Phonetic / doubleMetaphone analyzer — sound-alike matching
    [JsonPropertyName(MachineSearchIndexFields.TitlePhonetic)]
    public string TitlePhonetic { get; set; } = string.Empty;

    [JsonPropertyName(MachineSearchIndexFields.Manufacturer)]
    public string Manufacturer { get; set; } = string.Empty;

    // Partition-key form of manufacturer (lower-case) — used as a filter
    // term in Phase 2b queries to scope retrieval by manufacturer
    [JsonPropertyName(MachineSearchIndexFields.ManufacturerKey)]
    public string ManufacturerKey { get; set; } = string.Empty;

    // Collection(String) — empty today (OPDB data gap per issue #611),
    // modeled so the field exists in the index when data arrives
    [JsonPropertyName(MachineSearchIndexFields.Designers)]
    public List<string> Designers { get; set; } = [];

    [JsonPropertyName(MachineSearchIndexFields.Themes)]
    public List<string> Themes { get; set; } = [];

    [JsonPropertyName(MachineSearchIndexFields.Year)]
    public int? Year { get; set; }

    [JsonPropertyName(MachineSearchIndexFields.GroupId)]
    public string? GroupId { get; set; }

    [JsonPropertyName(MachineSearchIndexFields.EditionLabel)]
    public string? EditionLabel { get; set; }

    // Inline completeness score — proportion of data-quality signals present
    // on this Machine record. Drives the scoring-profile magnitude function.
    // Filterable (required for scoring functions per ADR-0049).
    // TODO (ADR-0049 phase 2b): reconcile completeness to a shared MachineCompleteness helper once the
    // parallel branch that may introduce one lands.
    [JsonPropertyName(MachineSearchIndexFields.Completeness)]
    public double Completeness { get; set; }

    // LastSeenAt from OPDB sync — the most recent time this machine's canonical
    // record was confirmed by the OPDB API. Drives the scoring-profile freshness
    // function. Filterable (required for scoring functions).
    [JsonPropertyName(MachineSearchIndexFields.LastUpdatedUtc)]
    public DateTimeOffset LastUpdatedUtc { get; set; }
}
