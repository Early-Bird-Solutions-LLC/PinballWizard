using System.Text.Json.Serialization;

namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the searchCorpus Foundry function tool. Carries the
// citation-shaped projection of `RetrievedChunk[]` the agents see —
// the model needs `DocumentUrl` + `PageStart` + `PageEnd` +
// `SectionHeading` + `Content` to ground answers and the citation
// extractor needs the same surface to build `Citation` instances per
// ADR-0022 § Algorithm step 2.
//
// `Score` is re-threaded here (PR-C2) so the citation extractor can
// populate `Citation.RelevanceScore`, but decorated `[JsonIgnore]` so
// the model NEVER sees it. Exposing the score in the model-facing
// payload would tempt the model to compare scores in prose (meta-
// noise) or adjust its reasoning based on retrieval confidence — both
// are extractor + confidence-calculator concerns, not model concerns.
// The JSON contract test `SearchCorpusHitJsonContractTests` pins this
// invariant against silent removal of the attribute.
//
// `ChunkId` is dropped — the model has no use for it; the extractor
// keys citations on `DocumentId` to collapse multiple chunks from the
// same document. `Manufacturer` is dropped — it already appears in
// any prior `getMachineByTitle` ground truth and the chunk's
// `MachineTitle` is the disambiguating field.
public sealed record SearchCorpusResult(
    IReadOnlyList<SearchCorpusHit> Hits);

public sealed record SearchCorpusHit(
    string MachineId,
    string MachineTitle,
    string DocumentId,
    string DocumentUrl,
    string DocumentType,
    int PageStart,
    int PageEnd,
    string SectionHeading,
    string Content)
{
    // Re-threaded from `RetrievedChunk.Score` in PR-C2. The [JsonIgnore]
    // attribute is load-bearing: it prevents the score from appearing in
    // the JSON payload the model receives (both via FunctionResultContent
    // serialization and any direct JsonSerializer.Serialize call). Null
    // when the SDK did not return a relevance score (e.g. for a pure
    // keyword query that bypassed the semantic re-ranker).
    [JsonIgnore]
    public double? Score { get; init; }

    // Re-threaded from `RetrievedChunk.LastScrapedUtc` in PR-C3. The
    // [JsonIgnore] attribute is load-bearing for the same reason as Score:
    // citation freshness is a user-facing signal, not a reasoning input for
    // the model. The frontend CitationCard reads LastScrapedUtc from
    // Citation (not from the model's function-result payload) — consistent
    // with ADR-0026 § 4. Null when the chunk was indexed before PR-C3 or
    // when the source document's scraper did not record LastDownloadedAt.
    [JsonIgnore]
    public DateTimeOffset? LastScrapedUtc { get; init; }
}
