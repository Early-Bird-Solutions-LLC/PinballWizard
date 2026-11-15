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
//      If stripping intent would empty the query (e.g. "pinball wizard"),
//      keep the intent tokens — they are the name (#780 Bugbot Medium).
//   3. Post-filter hits with TitleMatchesQuery ONLY when fluff was stripped
//      (question-shaped). Bare / synonym / phonetic name typing must not
//      require lexical title overlap (#780 Bugbot High).
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

    // Result of normalizing a suggest query: tokens for AI Search, plus
    // whether lexical title overlap must be enforced on hits.
    public readonly record struct NormalizedSuggestQuery(
        IReadOnlyList<string> Tokens,
        bool RequireTitleTokenOverlap);

    // Full normalize path used by MachineSuggestService.
    public static NormalizedSuggestQuery Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new([], RequireTitleTokenOverlap: false);

        var lower = text.ToLowerInvariant();
        var raw = lower.Split(
            [' ', '\t', ',', '.', '!', '?', '(', ')', '-', '/', '\'', '"', ':', ';'],
            StringSplitOptions.RemoveEmptyEntries);

        var afterStopwords = new List<string>(raw.Length);
        var removedStopwords = false;
        foreach (var tok in raw)
        {
            if (tok.Length <= 1)
                continue;
            if (StopWords.Contains(tok))
            {
                removedStopwords = true;
                continue;
            }

            afterStopwords.Add(tok);
        }

        var removedIntent = false;
        var tokens = afterStopwords;
        if (afterStopwords.Count > 1)
        {
            var withoutIntent = afterStopwords
                .Where(static t => !QuestionIntentWords.Contains(t))
                .ToList();

            // If every remaining token is also an intent word (e.g. "pinball
            // wizard"), keep them — they are the machine name, not fluff.
            if (withoutIntent.Count > 0)
            {
                if (withoutIntent.Count < afterStopwords.Count)
                    removedIntent = true;
                tokens = withoutIntent;
            }
        }

        // Post-filter only when we actually stripped question fluff. Bare
        // name / abbreviation / typo queries rely on synonym + phonetic
        // index fields and must not demand lexical title overlap.
        var requireOverlap = (removedStopwords || removedIntent) && tokens.Count > 0;
        return new(tokens, requireOverlap);
    }

    // Tokenizes free text for suggest (tokens only). Prefer Normalize when
    // the caller also needs the post-filter gate.
    public static IReadOnlyList<string> Tokenize(string text)
        => Normalize(text).Tokens;

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

        return raw
            .Where(static tok => tok.Length > 1 && !StopWords.Contains(tok))
            .ToList();
    }
}
