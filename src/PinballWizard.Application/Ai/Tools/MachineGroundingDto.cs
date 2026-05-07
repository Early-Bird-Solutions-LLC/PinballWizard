namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the getMachineByTitle Foundry function tool.
// Carries every field the Wizard / Valuation / Rules / Repair sub-agents
// need from a single Machine record plus the citation surface — OpdbId
// + OpdbSourceUrl pin the citation chain back to OPDB so the agent's
// answer can include a link directly.
//
// Phase 3 Wave 2 PR 5 introduces the type. Phase 4 will add a sibling
// CorpusChunkGroundingDto for searchCorpus (RAG retrieval); the agents
// will see both as separate tools.
public sealed record MachineGroundingDto(
    string OpdbId,
    string Title,
    string Manufacturer,
    int? Year,
    IReadOnlyList<string> Themes,
    IReadOnlyList<string> Designers,
    string? OpdbSourceUrl,
    IReadOnlyList<MachineEditionGroundingDto> Editions);

public sealed record MachineEditionGroundingDto(
    string Name,
    string? Msrp,
    string? Availability,
    string? Description);
