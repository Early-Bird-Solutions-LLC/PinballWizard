namespace PinballWizard.Application.Resolution;

// Shape of data/seeds/machine_aliases.v1.json. Human-curated entries ONLY — machine-derived
// variants (scraper slugs, OPDB edition/manufacturer tokens) flow automatically and are never seeded.
public sealed record MachineAliasSeedFile(int Version, IReadOnlyList<MachineAliasEntry> Aliases);

public sealed record MachineAliasEntry(
    string Alias,
    string? OpdbGroupId,   // preferred: alias resolves to the whole edition family
    string? MachineId,     // only for edition-specific aliases
    string ManufacturerKey,
    string? Notes,
    string? AddedBy);
