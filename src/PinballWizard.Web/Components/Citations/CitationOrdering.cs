using PinballWizard.Application.Ai;

namespace PinballWizard.Web.Components.Citations;

/// <summary>
/// Shared ordering logic for citation cards and inline markers.
///
/// <para>
/// <see cref="InRenderOrder"/> delegates to <see cref="CitationStrip.BuildGroups"/>
/// and flattens the result, so it is impossible for the cascade (marker tooltips)
/// to drift from the card numbering sequence. Card N and marker N always agree by
/// construction; <c>InRenderOrder_matches_card_ordinal_order</c> asserts it.
/// </para>
/// </summary>
public static class CitationOrdering
{
    /// <summary>
    /// Returns <paramref name="citations"/> in the exact order the cards are
    /// numbered by <see cref="CitationStrip"/>: index 0 = card N=1, etc.
    /// Delegates to <see cref="CitationStrip.BuildGroups"/> so the two cannot drift.
    /// </summary>
    public static IReadOnlyList<Citation> InRenderOrder(IReadOnlyList<Citation> citations)
        => [.. CitationStrip.BuildGroups(citations).SelectMany(g => g.Citations)];
}
