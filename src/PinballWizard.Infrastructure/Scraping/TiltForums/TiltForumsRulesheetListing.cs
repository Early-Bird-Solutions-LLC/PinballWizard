namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// One entry from the Tilt Forums "Rulesheet Master List" wiki page — a
/// game title, the manufacturer section it's grouped under, and the topic
/// URL to fetch the full rulesheet from.
/// </summary>
public sealed class TiltForumsRulesheetListing
{
    /// <summary>Game title as it appears in the master list table (already clean — no "Rulesheet" suffix).</summary>
    public required string GameTitle { get; init; }

    /// <summary>The manufacturer section heading text this listing was found under (e.g. "Stern Pinball").</summary>
    public required string ManufacturerHeaderText { get; init; }

    /// <summary>Full URL of the Discourse topic containing the rulesheet.</summary>
    public required string TopicUrl { get; init; }
}
