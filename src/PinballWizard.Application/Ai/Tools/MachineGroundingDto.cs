namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the getMachineByTitle Foundry function tool.
// Carries every field the Wizard / Valuation / Rules / Repair sub-agents
// need from a single Machine record plus the citation surface — OpdbId
// + OpdbSourceUrl pin the citation chain back to OPDB so the agent's
// answer can include a link directly.
//
// S5 (ADR-0029): GroupId and Siblings surface the sibling base-machine
// records that share the same leading OPDB group segment so the agent
// can enumerate distinct versions (Pro / Premium / LE) and ask one
// targeted clarifying question when the question is version-dependent.
// Siblings includes only same-group base machines and is empty (not
// null) when the machine has no group id or is the sole member of its
// group. The primary resolved machine is NOT repeated in Siblings.
public sealed record MachineGroundingDto(
    string OpdbId,
    string Title,
    string Manufacturer,
    int? Year,
    IReadOnlyList<string> Themes,
    IReadOnlyList<string> Designers,
    string? OpdbSourceUrl,
    IReadOnlyList<MachineEditionGroundingDto> Editions,
    string? GroupId,
    IReadOnlyList<MachineSiblingGroundingDto> Siblings);

public sealed record MachineEditionGroundingDto(
    string Name,
    string? Msrp,
    string? Availability,
    string? Description);

// A sibling base-machine record within the same OPDB group — a distinct
// Pro / Premium / LE / Collector edition of the same franchise title.
// Carries only the fields the agent needs to enumerate editions for a
// clarifying question: OpdbId (citation anchor), Title (display name),
// Year, and Editions (per-sibling MSRP / availability data).
public sealed record MachineSiblingGroundingDto(
    string OpdbId,
    string Title,
    int? Year,
    IReadOnlyList<MachineEditionGroundingDto> Editions);