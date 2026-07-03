namespace PinballWizard.Application.Catalog;

public sealed record ManufacturerCatalogStats(
    string Manufacturer,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<MachineDocStats> Machines)
{
    // Human-readable manufacturer name (e.g. "Stern Pinball") carried on the rollup so
    // consumers can render/link a manufacturer without a separate machine read. Null on
    // rollup docs written before this field existed (pre-backfill) — consumers degrade to
    // the `Manufacturer` key. `Manufacturer` stays the partition key (e.g. "stern").
    public string? ManufacturerDisplayName { get; init; }
}

public sealed record MachineDocStats(
    string MachineId,
    string Title,
    string? EditionLabel,
    string? GroupId,
    int? Year,
    bool IsOpdbOnly,                       // no manufacturer-scraper slug → expected gap signal
    int DocCount,
    IReadOnlyDictionary<string, int> DocTypeCounts,
    bool HasManual);
