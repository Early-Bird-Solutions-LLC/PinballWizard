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
// Wave 2 deferred: LastScrapedUtc (PR-C3, AI Search index field add)
//   and RelevanceScore (PR-C2, re-thread Score onto SearchCorpusHit).
//   Both remain null in Wave 1 — the frontend CitationCard must
//   tolerate null freshness/score gracefully.
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
    double? RelevanceScore = null);
