namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// One discovered Tilt Forums rulesheet listing — a game title, the topic
/// URL to fetch the full rulesheet from, and (for master-list entries) the
/// manufacturer section it's grouped under. Subcategory-discovered entries
/// carry a null <see cref="ManufacturerHeaderText"/> (no manufacturer hint).
/// </summary>
public sealed class TiltForumsRulesheetListing
{
    /// <summary>Game title as it appears in the master list table (already clean — no "Rulesheet" suffix).</summary>
    public required string GameTitle { get; init; }

    /// <summary>Manufacturer section header from the master list, or null for subcategory-only topics.</summary>
    public string? ManufacturerHeaderText { get; init; }

    /// <summary>Full URL of the Discourse topic containing the rulesheet.</summary>
    public required string TopicUrl { get; init; }
}
