namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the searchCorpus Foundry function tool. Carries the
// citation-shaped projection of `RetrievedChunk[]` the agents see —
// the model needs `DocumentUrl` + `PageStart` + `PageEnd` +
// `SectionHeading` + `Content` to ground answers and the citation
// extractor needs the same surface to build `Citation` instances per
// ADR-0022 § Algorithm step 2.
//
// `Score` from `RetrievedChunk` is intentionally dropped — re-rank
// scoring is an extractor + confidence-calculator concern, not
// something the model needs to see (and exposing it would tempt the
// model to compare scores in prose, which is meta-noise). `ChunkId`
// is dropped likewise — the model has no use for it; the extractor
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
    string Content);
