using System.Text.Json;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

// Design credits and the primary backglass URL taken from the Match Play
// OPDB V2 export. The V1 catalog file does not carry either. Keyed by opdb id.
public sealed record OpdbV2Enrichment(IReadOnlyList<string> Designers, string? ImageUrl);

public static class OpdbV2EnrichmentParser
{
    public const string DesignRole = "design";

    // Parses opdb-v2.json. Only machine rows are kept; group and alias rows
    // are not copied onto a different id. A missing entries array is a wrong
    // document, not an empty catalog.
    public static IReadOnlyDictionary<string, OpdbV2Enrichment> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "OPDB v2 export has no entries array. Expected https://mp-data.sfo3.cdn.digitaloceanspaces.com/opdb-v2.json.");
        }

        var map = new Dictionary<string, OpdbV2Enrichment>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var entryType = ReadString(entry, "entryType");
            if (!string.Equals(entryType, "machine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var opdbId = ReadString(entry, "opdbId");
            if (string.IsNullOrWhiteSpace(opdbId))
            {
                continue;
            }

            map[opdbId] = new OpdbV2Enrichment(ReadDesigners(entry), ReadBackglassUrl(entry));
        }

        return map;
    }

    private static List<string> ReadDesigners(JsonElement entry)
    {
        if (!entry.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var named = new List<(int Index, string Name)>();
        foreach (var person in people.EnumerateArray())
        {
            var role = ReadString(person, "role");
            if (!string.Equals(role, DesignRole, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ReadString(person, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var index = person.TryGetProperty("index", out var indexEl) && indexEl.TryGetInt32(out var i)
                ? i
                : int.MaxValue;
            named.Add((index, name.Trim()));
        }

        return named
            .OrderBy(p => p.Index)
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // Primary backglass, then any backglass. Large URL, then medium, then small.
    private static string? ReadBackglassUrl(JsonElement entry)
    {
        if (!entry.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (var image in images.EnumerateArray())
        {
            var type = ReadString(image, "type");
            if (!string.Equals(type, "backglass", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = FirstUrl(image);
            if (url is null)
            {
                continue;
            }

            var primary = image.TryGetProperty("primary", out var primaryEl)
                && primaryEl.ValueKind == JsonValueKind.True;
            if (primary)
            {
                return url;
            }

            fallback ??= url;
        }

        return fallback;
    }

    private static string? FirstUrl(JsonElement image)
    {
        if (!image.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var size in new[] { "large", "medium", "small" })
        {
            var url = ReadString(urls, size);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class OpdbV2EnrichmentApplicator
{
    // Designers replace only when V2 names at least one design credit, so a
    // machine with no design row keeps whatever the catalog already stored.
    // ImageUrl is set only when a backglass URL was present.
    public static void Apply(Machine machine, IReadOnlyDictionary<string, OpdbV2Enrichment> byOpdbId)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(byOpdbId);

        if (!byOpdbId.TryGetValue(machine.Id, out var extra))
        {
            return;
        }

        if (extra.Designers.Count > 0)
        {
            machine.Designers = extra.Designers.ToList();
        }

        if (!string.IsNullOrWhiteSpace(extra.ImageUrl))
        {
            machine.ImageUrl = extra.ImageUrl;
        }
    }
}
