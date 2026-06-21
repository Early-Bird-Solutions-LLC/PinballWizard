using System.Globalization;
using System.Text.RegularExpressions;

namespace PinballWizard.Application.Ai.Citations;

/// Rewrites model-emitted [[cite:k]] markers (k = searchCorpus source ordinal) into
/// [[cite:N]] (N = citation card ordinal). Unmatched markers are dropped (OBS-01:
/// never render a fake marker). Pure + deterministic — no Foundry, fully unit-tested.
public static class InlineCitationReconciler
{
    public sealed record ReconcileResult(
        string RewrittenText,
        IReadOnlySet<int> MarkedOrdinals,
        int TotalTokens, int RenderedTokens, int DroppedTokens);

    // Numeric payload only; non-numeric [[cite:x]] is not a cite token (left literal).
    private static readonly Regex CiteToken = new(@"\[\[cite:(\d+)\]\]", RegexOptions.Compiled);

    private static string Normalize(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    public static ReconcileResult Reconcile(
        string answerText,
        IReadOnlyList<Citation> citations,
        IReadOnlyList<string> sourceIndex)
    {
        // SourceUrl -> card ordinal N (1-based render order). First wins on dup URLs.
        var urlToOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < citations.Count; i++)
            urlToOrdinal.TryAdd(Normalize(citations[i].SourceUrl), i + 1);

        var marked = new HashSet<int>();
        int total = 0, rendered = 0, dropped = 0;

        var rewritten = CiteToken.Replace(answerText, m =>
        {
            total++;
            var k = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (k >= 1 && k <= sourceIndex.Count
                && urlToOrdinal.TryGetValue(Normalize(sourceIndex[k - 1]), out var n))
            {
                rendered++; marked.Add(n);
                return $"[[cite:{n}]]";
            }
            dropped++;
            return string.Empty; // drop the token (truthful-only)
        });

        return new ReconcileResult(rewritten, marked, total, rendered, dropped);
    }

    /// Removes all numeric [[cite:k]] markers from text. Used by the streaming
    /// path (next task) where the reconciler hasn't run yet and raw markers
    /// must be stripped before surfacing partial text to the client.
    public static string StripCiteTokens(string text) => CiteToken.Replace(text, string.Empty);
}
