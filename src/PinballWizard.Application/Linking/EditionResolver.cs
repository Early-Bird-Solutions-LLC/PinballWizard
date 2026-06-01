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
}
