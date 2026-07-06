namespace PinballWizard.Web.Engineering;

/// <summary>
/// Provides pre-parsed engineering docs and ADRs loaded once at startup from the
/// assembly's embedded resources. All members are safe to call on any thread with
/// no allocation beyond the initial construction (singleton lifetime).
/// </summary>
public interface IEngineeringDocsProvider
{
    /// <summary>Ordered list of engineering docs drawn from the embedded manifest.</summary>
    IReadOnlyList<EngineeringDoc> Docs { get; }

    /// <summary>Looks up a doc by slug (case-insensitive). Returns null if not found.</summary>
    EngineeringDoc? BySlug(string slug);

    /// <summary>All ADRs sorted by number ascending.</summary>
    IReadOnlyList<AdrEntry> Adrs { get; }

    /// <summary>Looks up an ADR by its number. Returns null if not found.</summary>
    AdrEntry? ByNumber(int number);

    /// <summary>Short git commit SHA stamped into the assembly at build time.</summary>
    string SourceCommit { get; }

    /// <summary>UTC build date (yyyy-MM-dd) stamped into the assembly at build time.</summary>
    string BuildDate { get; }
}
