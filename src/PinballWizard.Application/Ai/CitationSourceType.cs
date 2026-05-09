namespace PinballWizard.Application.Ai;

// Per ADR-0026 § 8. The Wizard cites different kinds of source: a
// canonical machine record (OPDB), a corpus chunk from a manual or
// service bulletin (RAG retrieval), or — in Phase 5+ — a curated link.
// The frontend CitationCard renders different visual treatments
// per source type (icon, color, secondary metadata). Tool-trace
// extractor populates this from the function-result type:
//   MachineGroundingDto         → MachineRecord
//   SearchCorpusResult.Hit      → CorpusChunk
//   regex-fallback OPDB URL     → MachineRecord
public enum CitationSourceType
{
    Unknown = 0,
    MachineRecord = 1,   // OPDB or other canonical machine catalog entry
    CorpusChunk = 2,     // manual / bulletin / curated doc chunk via searchCorpus
    CuratedLink = 3,     // Phase 5+ curated reference link, no DocumentChunkId
}
