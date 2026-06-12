namespace PinballWizard.Application.Ai;

// A single citation in a WizardAnswer. Per guardrails.md goal #5
// (provenance is sacred) and ADR-0026 § 8, every Wizard answer's
// citations must be resolvable back to a real source URL. Phase 3
// grounds against OPDB so MachineId + SourceUrl are populated; Phase 4
// RAG adds DocumentChunk-level citations enriched with page anchors,
// section heading, and source type classification.
//
// Wave 1 (PR-C1) populates: PageStart, PageEnd, SectionHeading,
//   SourceType (from ToolTraceCitationExtractor).
// Wave 2 PR-C2 populates: RelevanceScore (re-threads Score from
//   SearchCorpusHit; [JsonIgnore] keeps it model-invisible).
// Wave 2 PR-C3 deferred: LastScrapedUtc (requires AI Search index
//   field add + indexer projection — stays null until PR-C3 ships).
//   The frontend CitationCard must tolerate null freshness gracefully.
public sealed record Citation(
    string Title,
    string SourceUrl,
    string? MachineId = null,
    string? DocumentChunkId = null,
    int? PageStart = null,
    int? PageEnd = null,
    string? SectionHeading = null,
    CitationSourceType SourceType = CitationSourceType.Unknown,
    DateTimeOffset? LastScrapedUtc = null,
    double? RelevanceScore = null,
    // Multi-turn (2026-06-11): true when this citation was carried forward
    // from a prior conversation turn because the current turn answered from
    // conversation context without firing a retrieval tool (see the
    // inheritance block in AiRouter.ApplyPostAgentGuardrailsAsync). The UI
    // labels inherited citations so provenance display stays honest about
    // WHEN the grounding happened. Always false on single-shot answers.
    bool Inherited = false);
