namespace PinballWizard.Application.Resolution;

// Shape of data/seeds/machine_aliases.v1.json. Human-curated entries ONLY — machine-derived
// variants (scraper slugs, OPDB edition/manufacturer tokens) flow automatically and are never seeded.
public sealed record MachineAliasSeedFile(int Version, IReadOnlyList<MachineAliasEntry> Aliases);

// Exactly one of OpdbGroupId / MachineId must be set — the type cannot express that, so the
// loader (S5) MUST reject an entry with both null or both set, and MUST reject an alias whose
// group/machine does not exist in the catalog. A dangling alias does not mis-attribute anything,
// but it silently does nothing — and a seed entry that silently does nothing is the same class
// of lie as a test that asserts against a URL which does not exist (#758). Fail CI instead.
public sealed record MachineAliasEntry(
    string Alias,
    string? OpdbGroupId,   // preferred: alias resolves to the whole edition family
    string? MachineId,     // only for edition-specific aliases
    string ManufacturerKey,
    string? Notes,
    string? AddedBy);
