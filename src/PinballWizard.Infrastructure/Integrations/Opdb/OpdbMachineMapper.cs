using System.Globalization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Maps an <see cref="OpdbMachineDto"/> (wire shape) to the project's
/// <see cref="Machine"/> aggregate. Pure functions — no I/O.
/// </summary>
public static class OpdbMachineMapper
{
    /// <summary>
    /// Maps a single OPDB record to a <see cref="Machine"/>. Returns
    /// null if the record is missing required fields (no OPDB ID, no
    /// manufacturer name, or <see cref="OpdbMachineDto.IsMachine"/> is
    /// false). Sync orchestrator counts those as "skipped".
    /// </summary>
    public static Machine? Map(OpdbMachineDto dto, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!dto.IsMachine) return null;
        if (string.IsNullOrWhiteSpace(dto.OpdbId)) return null;
        if (dto.Manufacturer is null || string.IsNullOrWhiteSpace(dto.Manufacturer.Name)) return null;

        var manufacturerName = dto.Manufacturer.Name;
        var manufacturerKey = NormalizeManufacturerKey(dto.Manufacturer.ShortName ?? manufacturerName);

        return new Machine
        {
            Id = dto.OpdbId,
            PartitionKey = manufacturerKey,
            ManufacturerDisplayName = manufacturerName,
            Title = dto.CommonName ?? dto.Name ?? dto.OpdbId,
            Year = ParseYear(dto.ManufactureDate),
            Designers = dto.Designers.Where(d => !string.IsNullOrWhiteSpace(d.Name)).Select(d => d.Name!).ToList(),
            Themes = dto.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
            Editions = [],
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            OpdbSourceUrl = $"https://opdb.org/machines/{dto.OpdbId}",
            FirstSeenAt = now,
            LastSeenAt = now,
        };
    }

    /// <summary>
    /// Returns a stable lowercase manufacturer partition key (e.g.,
    /// <c>"stern"</c>, <c>"jjp"</c>, <c>"americanpinball"</c>) suitable
    /// for use as a Cosmos partition key.
    /// </summary>
    public static string NormalizeManufacturerKey(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        var trimmed = raw.Trim().ToLowerInvariant();

        // Common short-form mappings — extend as needed.
        var key = trimmed switch
        {
            var s when s.Contains("stern", StringComparison.Ordinal) => "stern",
            var s when s.Contains("jersey jack", StringComparison.Ordinal) || s == "jjp" => "jjp",
            var s when s.Contains("american pinball", StringComparison.Ordinal) => "americanpinball",
            var s when s.Contains("spooky", StringComparison.Ordinal) => "spooky",
            var s when s.Contains("multimorphic", StringComparison.Ordinal) => "multimorphic",
            var s when s.Contains("chicago gaming", StringComparison.Ordinal) || s == "cgc" => "cgc",
            var s when s.Contains("haggis", StringComparison.Ordinal) => "haggis",
            var s when s.Contains("pinball brothers", StringComparison.Ordinal) => "pinballbrothers",
            var s when s.Contains("dutch pinball", StringComparison.Ordinal) => "dutch",
            var s when s.Contains("barrels of fun", StringComparison.Ordinal) => "barrelsoffun",
            _ => Sanitize(trimmed),
        };

        return key;
    }

    /// <summary>
    /// Merges fresh OPDB data onto an existing <see cref="Machine"/>
    /// without disturbing fields the project owns (manufacturer slugs,
    /// editions, first-seen timestamp). Used on sync upsert when a
    /// matching machine already exists in the repository.
    /// </summary>
    public static void MergeOpdbFieldsInto(Machine existing, OpdbMachineDto dto, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.Manufacturer?.Name is { } mfgName) existing.ManufacturerDisplayName.GetType(); // no-op: ManufacturerDisplayName is init-only and rarely changes

        existing.Title = dto.CommonName ?? dto.Name ?? existing.Title;
        existing.Year = ParseYear(dto.ManufactureDate) ?? existing.Year;

        if (dto.Designers.Count > 0)
        {
            existing.Designers = dto.Designers.Where(d => !string.IsNullOrWhiteSpace(d.Name)).Select(d => d.Name!).ToList();
        }

        if (dto.Keywords.Count > 0)
        {
            existing.Themes = dto.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        }

        existing.LastSeenAt = now;
    }

    private static int? ParseYear(string? manufactureDate)
    {
        if (string.IsNullOrWhiteSpace(manufactureDate)) return null;
        if (DateTime.TryParse(manufactureDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            return date.Year;
        }
        // Fall back to the leading 4-digit token, which covers cases like "1999" alone.
        if (manufactureDate.Length >= 4 && int.TryParse(manufactureDate.AsSpan(0, 4), CultureInfo.InvariantCulture, out var year))
        {
            return year;
        }
        return null;
    }

    private static string Sanitize(string raw)
    {
        var chars = raw.Where(c => char.IsLetterOrDigit(c)).ToArray();
        return chars.Length > 0 ? new string(chars) : "unknown";
    }
}
