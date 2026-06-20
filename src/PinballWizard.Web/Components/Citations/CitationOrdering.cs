using PinballWizard.Application.Ai;

namespace PinballWizard.Web.Components.Citations;

/// <summary>
/// Shared ordering logic for citation cards and inline markers.
///
/// <para>
/// <see cref="InRenderOrder"/> produces the exact flattened list that
/// <see cref="CitationStrip"/> uses to number its cards: groups sorted by
/// max RelevanceScore descending (nulls last), within each group sorted by
/// RelevanceScore descending, then flattened. Card N and marker N therefore
/// always agree — tooltip derivation in <see cref="CitationMarker"/> reads
/// <c>OrderedCitations[N-1]</c> with zero positional drift.
/// </para>
/// </summary>
public static class CitationOrdering
{
    /// <summary>
    /// Returns <paramref name="citations"/> in the exact order the cards are
    /// numbered by <see cref="CitationStrip"/>: index 0 = card N=1, etc.
    /// </summary>
    public static IReadOnlyList<Citation> InRenderOrder(IReadOnlyList<Citation> citations)
    {
        return [.. citations
            .GroupBy(c => ExtractHost(c.SourceUrl))
            .Select(g => new
            {
                MaxScore = g.Max(c => c.RelevanceScore ?? double.MinValue),
                Sorted   = g.OrderByDescending(c => c.RelevanceScore ?? double.MinValue).ToList(),
            })
            .OrderByDescending(g => g.MaxScore)
            .SelectMany(g => g.Sorted)];
    }

    private static string ExtractHost(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return "(unknown source)";

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return uri.Host;

        return sourceUrl;
    }
}
