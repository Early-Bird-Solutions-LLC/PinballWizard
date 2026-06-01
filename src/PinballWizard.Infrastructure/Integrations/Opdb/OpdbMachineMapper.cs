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
    /// null if the record is not a base machine (alias, missing OPDB ID,
    /// or missing manufacturer name). Aliases (variant/LE editions) are
    /// folded into their base machine's <see cref="Machine.Editions"/>
    /// list by <see cref="MapToEdition"/> in a second pass. Sync
    /// orchestrator counts non-mappable records as "skipped".
    /// </summary>
    /// <param name="groupTitle">
    /// The clean franchise title resolved once per OPDB group segment
    /// from the <c>is_machine_group</c> record (see ADR-0029 D1). When
    /// the record's own <c>common_name</c> is blank — true for modern
    /// Stern records, which would otherwise produce an edition-suffixed
    /// title like "Godzilla (Pro)" — the group title supplies the clean
    /// name. Null when the caller could not resolve a group record
    /// (singleton machines, or OPDB 404 on the segment); the existing
    /// <c>name</c>/<c>opdbId</c> fallback then applies, unchanged.
    /// </param>
    public static Machine? Map(OpdbMachineDto dto, DateTimeOffset now, string? groupTitle = null)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!dto.IsMachine) return null;
        if (string.IsNullOrWhiteSpace(dto.OpdbId)) return null;
        if (dto.Manufacturer is null || string.IsNullOrWhiteSpace(dto.Manufacturer.Name)) return null;

        var manufacturerName = dto.Manufacturer.Name;
        var manufacturerKey = NormalizeManufacturerKey(FirstNonBlank(dto.Manufacturer.ShortName, manufacturerName)!);
        var editionLabel = ExtractEditionLabel(dto.Name, dto.Features);

        return new Machine
        {
            Id = dto.OpdbId,
            PartitionKey = manufacturerKey,
            ManufacturerDisplayName = manufacturerName,
            // Title precedence (ADR-0029 D1): OPDB common_name when
            // present; else the clean group title; else the edition-
            // suffixed name; else the OPDB ID. The group title slots
            // *between* common_name and name so a populated common_name
            // is never overridden, but an empty one (modern Stern) gets
            // the clean franchise name instead of "Godzilla (Pro)".
            Title = FirstNonBlank(dto.CommonName, groupTitle, dto.Name) ?? dto.OpdbId,
            GroupId = ExtractGroupSegment(dto.OpdbId),
            Year = ParseYear(dto.ManufactureDate),
            Designers = dto.Designers.Where(d => !string.IsNullOrWhiteSpace(d.Name)).Select(d => d.Name!).ToList(),
            Themes = dto.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
            Editions = [],
            EditionLabel = editionLabel,
            EditionTokens = DeriveEditionTokens(editionLabel),
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            OpdbSourceUrl = $"https://opdb.org/machines/{dto.OpdbId}",
            FirstSeenAt = now,
            LastSeenAt = now,
        };
    }

    /// <summary>
    /// The OPDB group segment — the leading part of an OPDB ID before
    /// the first hyphen (e.g. <c>GweeP-MW95j</c> → <c>GweeP</c>). A
    /// relational key for sibling discovery per ADR-0029, never a merge
    /// key. Returns null if the ID has no hyphen (defensive — well-formed
    /// OPDB machine IDs always have at least two segments).
    /// </summary>
    public static string? ExtractGroupSegment(string opdbId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
        var firstHyphen = opdbId.IndexOf('-', StringComparison.Ordinal);
        return firstHyphen <= 0 ? null : opdbId[..firstHyphen];
    }

    /// <summary>
    /// The parenthetical of an edition-qualified OPDB name: "Godzilla (Pro)" → "Pro",
    /// "Godzilla (Premium/LE)" → "Premium/LE". Falls back to the joined features when
    /// the name has no parenthetical. Null when neither yields an edition label.
    /// </summary>
    public static string? ExtractEditionLabel(string? name, IReadOnlyList<string>? features)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var open = name.LastIndexOf('(');
            var close = name.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                var inner = name[(open + 1)..close].Trim();
                if (inner.Length > 0) return inner;
            }
        }
        if (features is { Count: > 0 })
        {
            var labels = features
                .Select(f => f.Replace(" edition", "", StringComparison.OrdinalIgnoreCase).Trim())
                .Where(f => f.Length > 0);
            var joined = string.Join("/", labels);
            if (joined.Length > 0) return joined;
        }
        return null;
    }

    /// <summary>
    /// Normalized lowercase tokens from an edition label: "Premium/LE" →
    /// ["premium","le"], "Pro" → ["pro"], "70th Anniversary" → ["70th"]. Splits on
    /// '/' and whitespace, drops noise words. Alias-fold (OpdbSyncService pass 2)
    /// appends more tokens later.
    /// </summary>
    public static List<string> DeriveEditionTokens(string? editionLabel)
    {
        if (string.IsNullOrWhiteSpace(editionLabel)) return [];
        var tokens = new List<string>();
        foreach (var part in editionLabel.Split(['/', ' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim().ToLowerInvariant();
            if (t is "anniversary" or "edition" or "and") continue;
            if (t.Length == 0 || tokens.Contains(t)) continue;
            tokens.Add(t);
        }
        return tokens;
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
    /// Detects an OPDB alias record. OPDB aliases are variant editions
    /// (LE / Premium / collector cuts) of a base machine; they share the
    /// first two OPDB ID segments with their base and add a third. OPDB
    /// flags them with <c>is_alias=true</c> and omits <c>is_machine</c>
    /// entirely, so checking either signal independently is brittle: this
    /// helper requires <see cref="OpdbMachineDto.IsAlias"/>=true OR a
    /// 3-segment OPDB ID, which catches both signals.
    /// </summary>
    public static bool IsAlias(OpdbMachineDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.IsAlias) return true;
        if (string.IsNullOrWhiteSpace(dto.OpdbId)) return false;
        return CountSegments(dto.OpdbId) >= 3;
    }

    /// <summary>
    /// Returns the base machine's OPDB ID for an alias record by stripping
    /// the third (alias) segment. For example,
    /// <c>GRoz4-MrRPw-A97X1</c> → <c>GRoz4-MrRPw</c>. Returns null if the
    /// supplied OPDB ID does not have three segments (i.e., is not an alias
    /// in the OPDB sense).
    /// </summary>
    public static string? GetBaseMachineOpdbId(string aliasOpdbId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasOpdbId);

        var firstHyphen = aliasOpdbId.IndexOf('-', StringComparison.Ordinal);
        if (firstHyphen < 0) return null;
        var secondHyphen = aliasOpdbId.IndexOf('-', firstHyphen + 1);
        if (secondHyphen < 0) return null;
        return aliasOpdbId[..secondHyphen];
    }

    /// <summary>
    /// Maps an alias <see cref="OpdbMachineDto"/> to a
    /// <see cref="MachineEdition"/> suitable for appending to the base
    /// machine's <see cref="Machine.Editions"/>. Returns null if the
    /// alias's name is missing (no edition name to extract).
    /// </summary>
    /// <remarks>
    /// Edition-name extraction strategy: aliases are conventionally named
    /// <c>{Base Title} ({Edition Name})</c> — e.g.,
    /// <c>"Batman 66 (Super LE)"</c>. The parenthetical suffix is the
    /// edition name. If no parenthetical is present (rare in current OPDB
    /// data), the full name is used as the edition name.
    /// </remarks>
    public static MachineEdition? MapToEdition(OpdbMachineDto alias)
    {
        ArgumentNullException.ThrowIfNull(alias);

        var fullName = alias.Name;
        if (string.IsNullOrWhiteSpace(fullName)) return null;

        return new MachineEdition
        {
            Name = ExtractEditionName(fullName),
            Description = fullName,
            OpdbAliasId = alias.OpdbId,
            OpdbSourceUrl = string.IsNullOrWhiteSpace(alias.OpdbId)
                ? null
                : $"https://opdb.org/machines/{alias.OpdbId}",
        };
    }

    /// <summary>
    /// Merges fresh OPDB data onto an existing <see cref="Machine"/>
    /// without disturbing fields the project owns (manufacturer slugs,
    /// editions, first-seen timestamp). Used on sync upsert when a
    /// matching machine already exists in the repository.
    /// </summary>
    public static void MergeOpdbFieldsInto(Machine existing, OpdbMachineDto dto, DateTimeOffset now, string? groupTitle = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(dto);

        // Same title precedence as Map (ADR-0029 D1): a re-sync must
        // converge an existing edition-suffixed title to the clean group
        // title once the group record is resolvable.
        existing.Title = FirstNonBlank(dto.CommonName, groupTitle, dto.Name) ?? existing.Title;
        if (!string.IsNullOrWhiteSpace(dto.OpdbId))
        {
            existing.GroupId = ExtractGroupSegment(dto.OpdbId);
        }
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

    private static readonly Dictionary<string, IReadOnlyList<string>> ManufacturerMatchTokens =
        new(StringComparer.Ordinal)
        {
            ["stern"]           = ["stern"],
            ["jjp"]             = ["jjp", "jersey", "jack"],
            ["americanpinball"] = ["americanpinball", "american", "pinball", "ap"],
            ["spooky"]          = ["spooky"],
            ["multimorphic"]    = ["multimorphic"],
            ["cgc"]             = ["cgc", "chicago", "gaming"],
            ["haggis"]          = ["haggis"],
            ["pinballbrothers"] = ["pinballbrothers", "pinball", "brothers", "pb"],
            ["dutch"]           = ["dutch"],
            ["barrelsoffun"]    = ["barrelsoffun", "barrels", "fun", "bof"],
        };

    public static IReadOnlyList<string> GetMatchTokens(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ManufacturerMatchTokens.TryGetValue(key, out var tokens)
            ? tokens
            : [key];
    }

    // Returns the first argument that is non-null AND non-whitespace,
    // or null if every candidate is null/empty/whitespace. C#'s null-
    // coalescing operator (??) preserves empty strings — `null ?? "" ??
    // fallback` evaluates to `""`, not `fallback`. OPDB's /api/export
    // returns some modern Stern records (e.g., GweeP-MW95j — an OPDB
    // ID for Godzilla Pro 2021) with empty `name` strings, which
    // previously produced empty titles in Cosmos and broke title-keyed
    // grounding lookups (IMachineRepository.QueryByTitleAsync). This
    // helper makes the fallback chain treat blanks the same as nulls
    // so the documented OpdbId fallback actually fires.
    //
    // Visibility: internal so OpdbSyncService can use it on the alias
    // pass-2 path (the alias's ShortName/Name fallback has the same
    // bug shape, surfaced as a logged exception when the
    // resolved-blank manufacturer key fails NormalizeManufacturerKey's
    // ArgumentException.ThrowIfNullOrWhiteSpace gate).
    internal static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }
        return null;
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

    private static int CountSegments(string opdbId)
    {
        var count = 1;
        foreach (var c in opdbId)
        {
            if (c == '-') count++;
        }
        return count;
    }

    private static string ExtractEditionName(string fullName)
    {
        // OPDB convention: "{Base Title} ({Edition Name})". Take the inner
        // text of the *last* parenthesized group — covers titles that
        // themselves contain parens (rare but seen in the catalog).
        var openIdx = fullName.LastIndexOf('(');
        var closeIdx = fullName.LastIndexOf(')');
        if (openIdx >= 0 && closeIdx > openIdx + 1)
        {
            var inner = fullName[(openIdx + 1)..closeIdx].Trim();
            if (!string.IsNullOrWhiteSpace(inner)) return inner;
        }
        return fullName.Trim();
    }
}
