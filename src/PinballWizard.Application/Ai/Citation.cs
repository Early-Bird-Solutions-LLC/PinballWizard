namespace PinballWizard.Application.Ai;

// A single citation in a WizardAnswer. Per guardrails.md goal #5
// (provenance is sacred), every Wizard answer's citations must be
// resolvable back to a real source URL. Phase 3 grounds against OPDB so
// MachineId + SourceUrl are populated; Phase 4 RAG adds DocumentChunk-
// level citations using the same shape.
public sealed record Citation(
    string Title,
    string SourceUrl,
    string? MachineId = null,
    string? DocumentChunkId = null);
