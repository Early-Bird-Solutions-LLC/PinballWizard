using System.Text.RegularExpressions;

namespace PinballWizard.Application.Ai.Citations;

// Drops citations whose machine subject never appears in the answer prose
// (#783 / ADR-0062). ADR-0022 extracts every tool-trace hit; without this
// filter a recommendation can list Hobbit / Halloween while the Sources
// panel still shows "The Jetsons — Metadata" from an unused retrieval hit.
//
// Placement: after extract + multi-turn inheritance, before inline-marker
// reconcile and the confidence / NoCitation gates — so dropped citations
// cannot inflate citation-coverage or pass the binary ≥1 gate.
public static class AnswerCitedSubjectFilter
{
    public sealed record FilterResult(
        IReadOnlyList<Citation> Kept,
        int DroppedCount);

    // Titles look like "The Jetsons — Metadata", "Godzilla (p. 12–14)",
    // "Market value · Medieval Madness — Silverball Labs / …".
    private static readonly Regex EmDashSubject = new(
        @"^(?<subject>.+?)\s+[—–-]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PageRangeSuffix = new(
        @"\s*\(p\.\s*[^)]+\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MarketValuePrefix = new(
        @"^Market value\s*·\s*(?<subject>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // OPDB / Match Play titles without a human machine name — keep (fail
    // open). Filtering requires an extractable subject.
    private static readonly Regex OpdbRecordTitle = new(
        @"^OPDB record\s+\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MatchPlayTitle = new(
        @"^Match Play standings\s*·",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static FilterResult Filter(string answerText, IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);

        if (citations.Count == 0)
        {
            return new FilterResult(citations, DroppedCount: 0);
        }

        var answer = answerText ?? string.Empty;
        var kept = new List<Citation>(citations.Count);
        var dropped = 0;

        foreach (var citation in citations)
        {
            if (citation.Inherited || IsAnswerRelevant(citation, answer))
            {
                kept.Add(citation);
            }
            else
            {
                dropped++;
            }
        }

        return new FilterResult(kept, dropped);
    }

    internal static bool IsAnswerRelevant(Citation citation, string answerText)
    {
        var subject = ExtractSubject(citation.Title);
        if (subject is null)
        {
            return true; // no extractable subject → keep
        }

        return SubjectAppearsInAnswer(subject, answerText);
    }

    // Returns null when the title has no machine-name subject we can match
    // against answer prose (OPDB id-only, Match Play tournament name, bare
    // page-range titles). Null ⇒ keep.
    internal static string? ExtractSubject(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var trimmed = title.Trim();

        if (OpdbRecordTitle.IsMatch(trimmed) || MatchPlayTitle.IsMatch(trimmed))
        {
            return null;
        }

        var market = MarketValuePrefix.Match(trimmed);
        if (market.Success)
        {
            trimmed = market.Groups["subject"].Value.Trim();
            // Market titles still carry " — Silverball…" after the machine.
            var marketDash = EmDashSubject.Match(trimmed);
            if (marketDash.Success)
            {
                return NormalizeSubject(marketDash.Groups["subject"].Value);
            }

            return NormalizeSubject(PageRangeSuffix.Replace(trimmed, string.Empty));
        }

        var dash = EmDashSubject.Match(trimmed);
        if (dash.Success)
        {
            return NormalizeSubject(dash.Groups["subject"].Value);
        }

        var withoutPage = PageRangeSuffix.Replace(trimmed, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(withoutPage) || withoutPage.StartsWith("p.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return NormalizeSubject(withoutPage);
    }

    private static string? NormalizeSubject(string raw)
    {
        var subject = raw.Trim();
        if (subject.Length < 2)
        {
            return null;
        }

        return subject;
    }

    internal static bool SubjectAppearsInAnswer(string subject, string answerText)
    {
        if (string.IsNullOrWhiteSpace(answerText))
        {
            return false;
        }

        if (answerText.Contains(subject, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "The Jetsons" ↔ answer saying "Jetsons" without the article.
        var withoutArticle = StripLeadingArticle(subject);
        if (withoutArticle is not null
            && withoutArticle.Length >= 3
            && !string.Equals(withoutArticle, subject, StringComparison.OrdinalIgnoreCase)
            && answerText.Contains(withoutArticle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? StripLeadingArticle(string subject)
    {
        foreach (var article in new[] { "the ", "a ", "an " })
        {
            if (subject.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return subject[article.Length..].Trim();
            }
        }

        return null;
    }
}
