using System.Text.RegularExpressions;

namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Shared word-boundary edition-label matcher (AB#259 code-review). Both
// the R2 (AnsweredAllEditions) and R3 (HonestSubstitution) evaluators
// need to decide whether an answer NAMES a given edition. A naive
// answerText.Contains(label) produces substring false-positives that
// defeat the metric:
//   - "LE"  ⊂ "avai{LE}able"  → an answer saying "not available" would
//                                falsely register as naming the LE edition.
//   - "Pro" ⊂ "ap{pro}priate" / "{pro}cess" → falsely names the Pro edition.
//
// The fix: match each label as a WHOLE WORD (regex \b…\b). A slash-
// separated label ("Premium/LE") is split into sub-tokens first, then any
// sub-token matched word-bounded counts (the slash denotes alternative
// spellings of one base). \b treats hyphens as boundaries, so qualified
// phrases like "LE-specific" / "Pro edition" still match.
//
// Kept as one internal helper so the C# stays DRY; the Python mirror in
// EvaluatorPythonSpecs reimplements the SAME \b…\b rule (parity per the
// file's drift-discipline note).
internal static class EditionLabelMatcher
{
    // Word-boundary token cache keyed by the case-folded token. The eval
    // set has a tiny, fixed label vocabulary, so an unbounded cache is
    // fine and avoids recompiling a Regex per call.
    private static readonly Dictionary<string, Regex> TokenRegexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();

    // True when answerText names the edition as a whole word (any sub-token
    // of a slash-separated label suffices). Returns false for null/blank
    // labels or labels with no usable tokens.
    public static bool AnswerNamesEdition(string answerText, string edition)
    {
        ArgumentNullException.ThrowIfNull(answerText);

        if (string.IsNullOrWhiteSpace(edition))
        {
            return false;
        }

        var tokens = edition.Split(
            '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }
            if (GetTokenRegex(token).IsMatch(answerText))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex GetTokenRegex(string token)
    {
        lock (CacheLock)
        {
            if (TokenRegexCache.TryGetValue(token, out var cached))
            {
                return cached;
            }

            var pattern = $@"\b{Regex.Escape(token)}\b";
            var regex = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            TokenRegexCache[token] = regex;
            return regex;
        }
    }
}
