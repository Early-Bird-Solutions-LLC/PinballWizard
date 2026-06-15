namespace PinballWizard.Application.Catalog;

public sealed record ManufacturerCatalogStats(
    string Manufacturer,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<MachineDocStats> Machines);

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
