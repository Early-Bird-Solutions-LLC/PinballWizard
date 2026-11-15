using System.Text.RegularExpressions;

namespace PinballWizard.Application.Ai.Citations;

// Drops citations whose machine subject never appears in the answer prose
// (#783 / ADR-0063). ADR-0022 extracts every tool-trace hit; without this
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

    // ADR-0043 curated Kineticist titles are "Label · {machine}" — subject
    // is the right-hand machine name, not the full phrase.
    private static readonly Regex CuratedGuideMiddleDot = new(
        @"^(?:Kineticist tutorial|Game rules|Pinball primer)\s*·\s*(?<subject>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Corpus hits without MachineTitle are stamped "Section · …" — not a
    // machine subject; fail open (keep).
    private static readonly Regex SectionOnlyTitle = new(
        @"^Section\s*·",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // OPDB edition qualifiers (Pro / Premium / LE / …). Answers almost always
    // name the game without the edition; matching the full "Godzilla (Pro)"
    // string would drop every Pro/Premium corpus hit for a plain "Godzilla"
    // answer. Strip for subject matching only — the citation Title is unchanged.
    // Closed list mirrors OPDB edition labels used in linking/reconciliation
    // fixtures + DeriveEditionTokens anniversary forms (Pro, Premium/LE,
    // Vault Edition, Super LE, Collector's, 70th Anniversary, …).
    private static readonly Regex EditionSuffix = new(
        @"\s*\((?:Pro(?:\s+Vault(?:\s+Edition)?)?|Premium(?:/LE)?(?:\s+Vault(?:\s+Edition)?)?|Super\s+LE|LE|L/?E|Limited\s+Edition|Vault(?:\s+Edition)?|Collector(?:['\u2019]?s)?\s+Edition|\d+(?:st|nd|rd|th)(?:\s+Anniversary)?|Standard|Home|CE|SE|Deluxe|Special\s+Edition)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // OPDB / Match Play titles without a human machine name — keep (fail
    // open). Filtering requires an extractable subject.
    private static readonly Regex OpdbRecordTitle = new(
        @"^OPDB record\s+\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ADR-0061 / ADR-0062 live tools emit "Match Play {standings|detail|arenas|
    // floor|players} · {event}". Fail-open the whole family — these are
    // tournament names, not machine subjects.
    private static readonly Regex MatchPlayTitle = new(
        @"^Match Play\b",
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

        if (OpdbRecordTitle.IsMatch(trimmed)
            || MatchPlayTitle.IsMatch(trimmed)
            || SectionOnlyTitle.IsMatch(trimmed))
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

        var curated = CuratedGuideMiddleDot.Match(trimmed);
        if (curated.Success)
        {
            return NormalizeSubject(curated.Groups["subject"].Value);
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
        var subject = EditionSuffix.Replace(raw.Trim(), string.Empty).Trim();
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

        if (ContainsSubjectToken(answerText, subject))
        {
            return true;
        }

        // "The Jetsons" ↔ answer saying "Jetsons" without the article.
        // Length floor is 4 (not 3): stripping "The" from "The Who" yields
        // "Who", which matches the pronoun in almost every English answer —
        // the #783 false-keep Bugbot flagged. "Jetsons" / "Hobbit" clear it.
        var withoutArticle = StripLeadingArticle(subject);
        if (withoutArticle is not null
            && withoutArticle.Length >= 4
            && !string.Equals(withoutArticle, subject, StringComparison.OrdinalIgnoreCase)
            && ContainsSubjectToken(answerText, withoutArticle))
        {
            return true;
        }

        return false;
    }

    // Subjects shorter than 3 chars (Stern "IT") are English substrings under
    // OrdinalIgnoreCase Contains — "it" appears in nearly every answer. Require
    // a case-sensitive whole-token match so only an intentional "IT" keeps the
    // citation; longer titles keep the case-insensitive substring path.
    private static bool ContainsSubjectToken(string answerText, string subject)
    {
        if (subject.Length < 3)
        {
            return Regex.IsMatch(
                answerText,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(subject)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.CultureInvariant);
        }

        return answerText.Contains(subject, StringComparison.OrdinalIgnoreCase);
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
