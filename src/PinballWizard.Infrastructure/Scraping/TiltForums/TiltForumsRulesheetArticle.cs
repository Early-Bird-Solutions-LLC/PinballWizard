namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// A fetched Tilt Forums rulesheet — the wiki OP's extracted text plus
/// metadata, ready for synthesis into RAG chunks.
/// </summary>
public sealed class TiltForumsRulesheetArticle
{
    /// <summary>Game title, carried through from the originating <see cref="TiltForumsRulesheetListing"/>.</summary>
    public required string GameTitle { get; init; }

    /// <summary>Manufacturer section heading text, carried through from the listing, or null for subcategory-only topics.</summary>
    public string? ManufacturerHeaderText { get; init; }

    /// <summary>Full URL of the Discourse topic — the citation URL that rides every RAG answer sourced from this article.</summary>
    public required string TopicUrl { get; init; }

    /// <summary>Wiki post author's Discourse username.</summary>
    public required string Author { get; init; }

    /// <summary>Extracted, heading-preserved plain text of the wiki OP (headings prefixed with Markdown-style <c>##</c>/<c>###</c>/<c>####</c>).</summary>
    public required string BodyText { get; init; }

    /// <summary>The "Wiki Rulesheet based on Code Rev: X.XX" value, if present in the post body.</summary>
    public string? CodeRevision { get; init; }

    /// <summary>Original post timestamp, if parseable.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}
