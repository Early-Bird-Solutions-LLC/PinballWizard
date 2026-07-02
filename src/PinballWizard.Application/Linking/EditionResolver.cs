using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Linking;

/// <summary>
/// Which editions of a franchise a document applies to. <c>EditionSubset</c> has
/// no production emitter yet — reserved for the planned subset-resolution tier
/// (true cross-base subsets via link_text, Task 6+).
/// </summary>
public enum EditionScope { SingleEdition, EditionSubset, FranchiseWide }

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
        ("70th", "70th"), ("60th", "60th"), ("30th", "30th"),
        ("_pro_", "pro"), ("-pro-", "pro"),
        ("_le_", "le"), ("-le-", "le"),
        ("_prem", "premium"), ("-prem", "premium"), ("premium", "premium"),
        ("_sle_", "sle"), ("_ve_", "ve"), ("_vault_", "vault"), ("_brk_", "brk"),
    };

    // Hyphen/no-space forms catch slugified filenames; the spaced forms catch
    // real anchor text ("Godzilla Feature Matrix", "Godzilla Rule Sheet"), which
    // is the only group signal for ~35 game-page matrices/rulesheets (design §87).
    // Spaced markers require the full two-word phrase, so they stay conservative.
    private static readonly string[] GroupLevelMarkers =
    {
        "feature-matrix", "featurematrix", "feature matrix",
        "rulesheet", "rule-sheet", "rule sheet",
    };

    // Spaced word markers for discovery anchor text ("Guardians of the Galaxy Pro
    // Flyer"): the link text is padded with spaces before matching so a marker only
    // fires on a whole word, not inside another (" le " never matches "galaxy").
    // Ordered most-specific-first. Anniversary tokens are digit-prefixed, so a bare
    // substring is already unambiguous.
    private static readonly (string Marker, string Token)[] LinkTextMarkers =
    {
        ("70th", "70th"), ("60th", "60th"), ("30th", "30th"),
        (" pro ", "pro"),
        (" le ", "le"),
        (" premium ", "premium"), (" prem ", "premium"),
        (" sle ", "sle"), (" ve ", "ve"), (" vault ", "vault"),
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

    /// <summary>
    /// True when the document signals an all-editions document. Markers are
    /// matched in the filename and, when supplied, the discovery link text — a
    /// rulesheet whose href is opaque is often only identifiable by its anchor.
    /// </summary>
    public static bool IsGroupLevelDoc(string filename, string? linkText = null)
    {
        var combined = $"{filename} {linkText}";
        if (string.IsNullOrWhiteSpace(combined)) return false;
        var lower = combined.ToLowerInvariant();
        return GroupLevelMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolve a document to the edition-correct base machine within an edition
    /// family. Page-1 text (when present) is authoritative and overrides the
    /// filename token; group-level docs fan out to all bases; a candidate set
    /// with no edition signal at all is left unresolved (the caller keeps the
    /// document NotInCatalog for admin review rather than guess). The optional
    /// discovery link text lets a group-level doc whose marker lives only in the
    /// anchor ("Godzilla Feature Matrix") still fan out franchise-wide.
    /// </summary>
    public static EditionResolution Resolve(
        string filename, string? page1Text, IReadOnlyList<Machine> candidates, string? linkText = null)
    {
        if (candidates.Count == 0) return EditionResolution.Unresolved();
        if (candidates.Count == 1) return EditionResolution.ForSingleEdition(candidates[0]);
        if (IsGroupLevelDoc(filename, linkText)) return EditionResolution.FanOut(candidates);

        // Page-1 text is authoritative; then the filename token; then the discovery
        // link text. Abbreviated flyer filenames (e.g. "GOTG-Pro.pdf", "GOTG-LE.pdf")
        // carry an undelimited "-pro."/"-le." the filename markers don't catch, but
        // their anchor text ("Guardians of the Galaxy Pro Flyer") names the edition.
        var token = ExtractEditionFromPageText(page1Text)
            ?? ExtractEditionToken(filename)
            ?? ExtractEditionTokenFromLinkText(linkText);
        if (token is null)
        {
            return EditionResolution.Unresolved();
        }

        var match = candidates.FirstOrDefault(m =>
            m.EditionTokens.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)));

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

    // Extracts an edition token from the discovery anchor text. The caller applies
    // IsGroupLevelDoc(filename, linkText) before this, so group-level anchors
    // ("… Feature Matrix") have already fanned out and never reach here.
    private static string? ExtractEditionTokenFromLinkText(string? linkText)
    {
        if (string.IsNullOrWhiteSpace(linkText)) return null;
        var padded = $" {linkText.ToLowerInvariant()} ";
        foreach (var (marker, token) in LinkTextMarkers)
        {
            if (padded.Contains(marker, StringComparison.Ordinal)) return token;
        }
        return null;
    }
}

/// <summary>Outcome of resolving an edition-family candidate set against a document.</summary>
public sealed record EditionResolution(
    IReadOnlyList<Machine> Machines, bool IsGroupFanOut, bool IsUnresolved)
{
    /// <summary>
    /// The structural edition scope of this resolution: a group-level fan-out is
    /// FranchiseWide (applies to every base), a resolution to a strict subset of
    /// the family (more than one base, but not a fan-out) is EditionSubset, and a
    /// resolution to exactly one base is SingleEdition. An Unresolved resolution
    /// reports FranchiseWide as the safe default — the caller does not persist a
    /// scope for unresolved documents (they stay NotInCatalog).
    /// </summary>
    public EditionScope Scope => (IsGroupFanOut, IsUnresolved, Machines.Count) switch
    {
        (true, _, _) => EditionScope.FranchiseWide,   // group-level fan-out
        (_, true, _) => EditionScope.FranchiseWide,   // unresolved → safe default
        (_, _, > 1)  => EditionScope.EditionSubset,
        (_, _, 1)    => EditionScope.SingleEdition,
        _            => EditionScope.FranchiseWide,    // empty/defensive
    };

    /// <summary>The document resolved to one specific edition's base machine.</summary>
    public static EditionResolution ForSingleEdition(Machine m) => new([m], IsGroupFanOut: false, IsUnresolved: false);

    /// <summary>
    /// The document applies to a known subset of bases (more than one, but not the whole family).
    /// NOTE: no production caller yet — reserved for the planned subset-resolution tier (true
    /// cross-base subsets via link_text, Task 6+). Subset resolution is not live today.
    /// </summary>
    public static EditionResolution ForSubset(IReadOnlyList<Machine> bases) => new(bases, IsGroupFanOut: false, IsUnresolved: false);

    /// <summary>A group-level document fans out to every base in the family.</summary>
    public static EditionResolution FanOut(IReadOnlyList<Machine> all) => new(all, IsGroupFanOut: true, IsUnresolved: false);

    /// <summary>No edition signal — caller keeps the document NotInCatalog.</summary>
    public static EditionResolution Unresolved() => new([], IsGroupFanOut: false, IsUnresolved: true);
}
