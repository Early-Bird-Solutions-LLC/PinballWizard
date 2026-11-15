namespace PinballWizard.Application.Findability;

// Prepares free-text typeahead queries for the machine suggest index (#785).
//
// The landing ask-box sends the *entire question* to suggest ("whats a good
// strategy for transformers"). Without normalization, AI Search BM25 matches
// stopwords and question fluff against unrelated titles (Fore ← "for",
// No Good Gofers ← "good", Invasion Strategy ← "strategy").
//
// Pipeline:
//   1. Tokenize + strip structural stopwords.
//   2. When more than one token remains, also strip question-intent words
//      (strategy, tips, rules, …) so they cannot outrank short machine names
//      like Jaws / Tron. A bare name query "strategy" keeps that sole token.
//   3. Post-filter hits: title must share a remaining token (exact or prefix
//      ≥3 chars) so typing "transform…" still surfaces Transformers.
//
// Stopword list mirrors MachineGroundingTool.TokenStopWords (canonical
// machine-title vocabulary) plus typeahead-specific question contractions
// and filler. Kept here rather than reaching into the tool's `internal` set
// so Findability stays free of Ai.Tools coupling.
public static class MachineSuggestQueryNormalizer
{
    // Lowercase only; Tokenize lowercases input before membership checks.
    internal static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Shared with MachineGroundingTool.TokenStopWords (keep in sync).
        "a", "an", "the", "and", "or", "of", "in", "on", "at", "to",
        "for", "with", "by", "from", "is", "it", "its", "this", "that",
        "my", "me", "i", "what", "how", "when", "where", "which", "who",
        "can", "could", "would", "should", "will", "do", "does", "did",
        "be", "been", "was", "were", "are", "am", "has", "have", "had",
        "not", "no", "but", "if", "as", "so", "up", "out", "about",
        "into", "than", "more", "other", "after", "before", "just",
        "tell", "know", "get", "give", "show", "find", "want",
        // Typeahead / question-shaped extras (#785).
        // Apostrophe forms ("what's") split to "what"+"s" already; these
        // cover the apostrophe-less typed variants.
        "whats", "whos", "wheres", "whens", "hows",
        "good", "best", "great", "nice",
    };

    // Stripped only when ≥2 tokens remain after StopWords — so a bare name
    // query "strategy" / "rules" still finds Invasion Strategy / …Rules….
    // Addresses Bugbot: question fluff must not outrank short titles (Jaws).
    internal static readonly HashSet<string> QuestionIntentWords = new(StringComparer.Ordinal)
    {
        "strategy", "strategies", "tips", "tip", "rules", "rule",
        "manual", "manuals", "worth", "price", "value",
        "tournament", "mode", "modes", "multiball", "wizard",
        "similar", "games", "game", "machine", "machines",
        "pinball", "play", "playing", "playfield",
    };

    // Minimum length for prefix matching while the user is still typing
    // the entity word ("transform" → Transformers).
    internal const int MinPrefixMatchLength = 3;

    // Tokenizes free text for suggest: lowercase, split on whitespace /
    // punctuation, drop single-char tokens and stopwords, then (when the
    // query is question-shaped) drop question-intent words. Numerics kept.
    public static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lower = text.ToLowerInvariant();
        var raw = lower.Split(
            [' ', '\t', ',', '.', '!', '?', '(', ')', '-', '/', '\'', '"', ':', ';'],
            StringSplitOptions.RemoveEmptyEntries);

        var result = new List<string>(raw.Length);
        foreach (var tok in raw)
        {
            if (tok.Length <= 1) continue;
            if (StopWords.Contains(tok)) continue;
            result.Add(tok);
        }

        // Multi-token (question-shaped): strip intent fluff so it cannot
        // compete with short machine names on length. Sole remaining token
        // is kept even if it is also an intent word (name typing).
        if (result.Count > 1)
        {
            result.RemoveAll(static t => QuestionIntentWords.Contains(t));
        }

        return result;
    }

    // Search text sent to AI Search. Empty when every token was a stopword —
    // caller should return no suggestions (honest empty, no I/O).
    public static string BuildSearchText(IReadOnlyList<string> tokens)
        => tokens.Count == 0 ? string.Empty : string.Join(' ', tokens);

    // True when a hit title is on-topic for the normalized query tokens.
    // Exact token overlap OR prefix match (≥ MinPrefixMatchLength) so
    // mid-typing still surfaces the intended machine.
    public static bool TitleMatchesQuery(string title, IReadOnlyList<string> queryTokens)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(title))
            return false;

        // Title tokenization uses StopWords only (not QuestionIntentWords),
        // so titles like "Invasion Strategy" keep "strategy" for bare-name hits.
        var titleTokens = TokenizeTitle(title);
        if (titleTokens.Count == 0)
            return false;

        foreach (var q in queryTokens)
        {
            foreach (var t in titleTokens)
            {
                if (string.Equals(q, t, StringComparison.Ordinal))
                    return true;

                if (q.Length >= MinPrefixMatchLength &&
                    t.StartsWith(q, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    // Title-side tokenize: structural stopwords only — never strips intent
    // words that are part of a real machine name.
    private static List<string> TokenizeTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        var raw = lower.Split(
            [' ', '\t', ',', '.', '!', '?', '(', ')', '-', '/', '\'', '"', ':', ';'],
            StringSplitOptions.RemoveEmptyEntries);

        var result = new List<string>(raw.Length);
        foreach (var tok in raw)
        {
            if (tok.Length <= 1) continue;
            if (StopWords.Contains(tok)) continue;
            result.Add(tok);
        }

        return result;
    }
}
