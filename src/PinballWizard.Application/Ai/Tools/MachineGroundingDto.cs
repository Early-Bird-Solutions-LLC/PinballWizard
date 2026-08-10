namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the getMachineByTitle Foundry function tool.
// Carries every field the Wizard / Valuation / Rules / Repair sub-agents
// need from a single Machine record plus the citation surface. Base and
// edition OPDB ids/URLs stay adjacent to the facts they support so the
// agent never has to borrow the primary machine's provenance for a
// sibling or edition-specific claim.
//
// S5 (ADR-0029): GroupId and Siblings surface the sibling base-machine
// records that share the same leading OPDB group segment so the agent
// can enumerate distinct versions (Pro / Premium / LE) and ask one
// targeted clarifying question when the question is version-dependent.
// Siblings includes only same-group base machines and is empty (not
// null) when the machine has no group id or is the sole member of its
// group. The primary resolved machine is NOT repeated in Siblings.
//
// TitleCollisions surfaces machines from DIFFERENT OPDB groups that
// share the same franchise title (e.g. Sega Godzilla 1998 vs Stern
// Godzilla 2021 — different GroupIds, same lookup-row). Unlike Siblings
// (same-group, same manufacturer tier), TitleCollisions is cross-group
// and cross-manufacturer. Populated only via the lookup-row path; the
// cross-partition fallback path always yields an empty list. The
// matched machine itself and any machine already present in Siblings
// are excluded. Empty (not null) when there are no cross-group collisions.
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
    IReadOnlyList<MachineSiblingGroundingDto> Siblings,
    IReadOnlyList<MachineSiblingGroundingDto> TitleCollisions);

public sealed record MachineEditionGroundingDto(
    string Name,
    string? Msrp,
    string? Availability,
    string? Description)
{
    // Keep the edition evidence and its provenance together. These fields
    // are optional because manufacturer-enriched editions may have feature
    // data without an OPDB alias, while OPDB aliases may have provenance
    // without manufacturer-authored feature text.
    public IReadOnlyList<string> UniqueFeatures { get; init; } = [];
    public string? OpdbAliasId { get; init; }
    public string? OpdbSourceUrl { get; init; }
}

// A sibling base-machine record within the same OPDB group — a distinct
// Pro / Premium / LE / Collector edition of the same franchise title.
// Carries the fields the agent needs to enumerate and NAME editions for
// R1/R2/R3 edition reasoning (Task 7, AB#259): OpdbId + OpdbSourceUrl
// (citation anchor), Title (display name), Manufacturer + Year
// (disambiguation), Editions (per-sibling facts), plus EditionLabel +
// EditionTokens.
//
// EditionLabel is the edition-qualified OPDB label for this base when it
// shares a franchise — e.g. "Pro", "Premium/LE" — so the Wizard can name
// the edition in a per-edition answer ("For the Pro edition …") rather
// than guessing from the Title (which stays the clean franchise name per
// ADR-0029 D1). EditionTokens are the normalized tokens this base answers
// to (e.g. ["premium","le","70th"]) — the discriminator the Wizard uses
// to match a user-named edition to the right sibling, and the same token
// the linker stamps on each chunk's `edition` field. Null EditionLabel /
// empty EditionTokens for singleton machines with no group siblings.
public sealed record MachineSiblingGroundingDto(
    string OpdbId,
    string Title,
    int? Year,
    IReadOnlyList<MachineEditionGroundingDto> Editions,
    string? EditionLabel,
    IReadOnlyList<string> EditionTokens)
{
    // TitleCollisions must identify candidates by manufacturer + year, and
    // every sibling/collision needs its own provenance rather than borrowing
    // the primary machine's OPDB URL.
    public string Manufacturer { get; init; } = string.Empty;
    public string? OpdbSourceUrl { get; init; }
}
