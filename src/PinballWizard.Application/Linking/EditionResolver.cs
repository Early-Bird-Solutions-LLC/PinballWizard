using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Linking;

/// <summary>
/// Resolves a candidate set of OPDB base machines that form an edition family
/// (same franchise, manufacturer, group segment, and year) plus a document, to
/// the single edition-correct base machine — using the document's filename
/// edition token and, when available, authoritative page-1 text. Group-level
/// documents (feature matrix, rulesheet) fan out to every base in the family.
/// Pure / no I/O — the linker supplies the page text.
/// </summary>
public static class EditionResolver
{
    // Ordered most-specific-first so "_le_pre_" matches "le" before "premium".
    private static readonly (string Marker, string Token)[] FilenameMarkers =
    {
        ("70th", "70th"),
        ("_pro_", "pro"), ("-pro-", "pro"),
        ("_le_", "le"), ("-le-", "le"),
        ("_prem", "premium"), ("-prem", "premium"), ("premium", "premium"),
    };

    private static readonly string[] GroupLevelMarkers =
    {
        "feature-matrix", "featurematrix", "rulesheet", "rule-sheet",
    };

    /// <summary>
    /// Returns a normalized edition token from a filename, or null if the
    /// filename has no edition marker or is a group-level document.
    /// </summary>
    public static string? ExtractEditionToken(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var lower = filename.ToLowerInvariant();
        if (IsGroupLevelDoc(lower)) return null;
        foreach (var (marker, token) in FilenameMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal)) return token;
        }
        return null;
    }

    /// <summary>True when the filename signals an all-editions document.</summary>
    public static bool IsGroupLevelDoc(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        var lower = filename.ToLowerInvariant();
        return GroupLevelMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    // Edition token → marker words expected (case-insensitively) in a candidate
    // base machine's edition-qualified Title. "le" and "premium" both accept the
    // combined "Premium/LE" base title OPDB uses for the Premium/LE machine.
    private static readonly Dictionary<string, string[]> TokenTitleMarkers = new(StringComparer.Ordinal)
    {
        ["pro"]     = ["pro"],
        ["le"]      = ["premium/le", "le", "premium"],
        ["premium"] = ["premium/le", "premium", "le"],
        ["70th"]    = ["70th", "anniversary"],
    };

    /// <summary>
    /// Resolve a document to the edition-correct base machine within an edition
    /// family. Page-1 text (when present) is authoritative and overrides the
    /// filename token; group-level docs fan out to all bases; a candidate set
    /// with no edition signal at all is left unresolved (the caller keeps the
    /// document NotInCatalog for admin review rather than guess).
    /// </summary>
    public static EditionResolution Resolve(
        string filename, string? page1Text, IReadOnlyList<Machine> candidates)
    {
        if (candidates.Count == 0) return EditionResolution.Unresolved();
        if (candidates.Count == 1) return EditionResolution.ForSingleEdition(candidates[0]);
        if (IsGroupLevelDoc(filename)) return EditionResolution.FanOut(candidates);

        // Page-1 text is authoritative; fall back to the filename token.
        var token = ExtractEditionFromPageText(page1Text) ?? ExtractEditionToken(filename);
        if (token is null || !TokenTitleMarkers.TryGetValue(token, out var markers))
        {
            return EditionResolution.Unresolved();
        }

        var match = candidates.FirstOrDefault(m =>
            markers.Any(marker => m.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)));

        return match is not null ? EditionResolution.ForSingleEdition(match) : EditionResolution.Unresolved();
    }

    private static string? ExtractEditionFromPageText(string? page1Text)
    {
        if (string.IsNullOrEmpty(page1Text)) return null;
        var lower = page1Text.ToLowerInvariant();
        if (lower.Contains("pro manual", StringComparison.Ordinal)) return "pro";
        if (lower.Contains("le manual", StringComparison.Ordinal)
            || lower.Contains("premium manual", StringComparison.Ordinal)) return "le";
        if (lower.Contains("70th", StringComparison.Ordinal)) return "70th";
        return null;
    }
}

/// <summary>Outcome of resolving an edition-family candidate set against a document.</summary>
public sealed record EditionResolution(
    IReadOnlyList<Machine> Machines, bool IsGroupFanOut, bool IsUnresolved)
{
    /// <summary>The document resolved to one specific edition's base machine.</summary>
    public static EditionResolution ForSingleEdition(Machine m) => new([m], IsGroupFanOut: false, IsUnresolved: false);

    /// <summary>A group-level document fans out to every base in the family.</summary>
    public static EditionResolution FanOut(IReadOnlyList<Machine> all) => new(all, IsGroupFanOut: true, IsUnresolved: false);

    /// <summary>No edition signal — caller keeps the document NotInCatalog.</summary>
    public static EditionResolution Unresolved() => new([], IsGroupFanOut: false, IsUnresolved: true);
}
