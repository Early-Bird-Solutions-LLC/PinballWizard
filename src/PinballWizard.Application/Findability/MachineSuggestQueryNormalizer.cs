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
//      If stripping intent would empty the query, keep name-capable intent
//      tokens (pinball/wizard) and drop pure fluff; if none are name-capable
//      ("strategy tips"), keep them for bare name typing (#785 Bugbot High).
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

    // Intent words that are also legitimate machine-name tokens (Pinball Wizard).
    // When stopword stripping leaves ONLY intent words, keep these and drop pure
    // fluff (strategy/tips/…) so "strategy for pinball wizard" cannot mark
    // Invasion Strategy as on-topic via TitleMatchesQuery (#785 Bugbot High).
    internal static readonly HashSet<string> NameCapableIntentWords = new(StringComparer.Ordinal)
    {
        "pinball", "wizard", "machine", "machines", "game", "games",
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

        var raw = SplitTokens(text);

        // Length-1 tokens never set removedStopwords (same as the prior loop:
        // single-char tokens are skipped before the StopWords membership check).
        var removedStopwords = raw.Any(static tok => tok.Length > 1 && StopWords.Contains(tok));
        var afterStopwords = raw
            .Where(static tok => tok.Length > 1 && !StopWords.Contains(tok))
            .ToList();

        var removedIntent = false;
        var tokens = afterStopwords;
        if (afterStopwords.Count > 1)
        {
            var withoutIntent = afterStopwords
                .Where(static t => !QuestionIntentWords.Contains(t))
                .ToList();

            if (withoutIntent.Count > 0)
            {
                if (withoutIntent.Count < afterStopwords.Count)
                    removedIntent = true;
                tokens = withoutIntent;
            }
            else
            {
                // All remaining tokens are intent words. Prefer name-capable
                // ones (pinball/wizard) over pure fluff (strategy/tips) so a
                // question about Pinball Wizard does not keep "strategy" and
                // TitleMatchesQuery-hit Invasion Strategy (#785 Bugbot High).
                // If none are name-capable ("strategy tips"), keep them for
                // bare name typing of Invasion Strategy / tip sheets.
                var nameCapable = afterStopwords
                    .Where(static t => NameCapableIntentWords.Contains(t))
                    .ToList();
                if (nameCapable.Count > 0)
                {
                    if (nameCapable.Count < afterStopwords.Count)
                        removedIntent = true;
                    tokens = nameCapable;
                }
            }
        }

        // PreferLexicalHits (RequireTitleTokenOverlap): question fluff was
        // stripped. MachineSuggestService prefers TitleMatchesQuery hits when
        // any exist, but MUST keep synonym/phonetic hits when none do —
        // "whats a good strategy for afm" still resolves Attack from Mars
        // (#785 Bugbot High). Bare abbreviations leave this false.
        var preferLexical = (removedStopwords || removedIntent) && tokens.Count > 0;
        return new(tokens, preferLexical);
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
        => SplitTokens(title)
            .Where(static tok => tok.Length > 1 && !StopWords.Contains(tok))
            .ToList();

    // Shared query/title splitter. Apostrophes are removed (not used as split
    // points) so "don't" → "dont" and "what's" → "whats" — splitting on `'`
    // used to emit "don" / "s", and "don" prefix-matched unrelated titles
    // (Donkey Kong, …) via TitleMatchesQuery (#785 Bugbot Medium).
    private static string[] SplitTokens(string text)
    {
        var lower = text.ToLowerInvariant()
            .Replace("'", "", StringComparison.Ordinal)
            .Replace("\u2019", "", StringComparison.Ordinal); // ’

        // Hyphens stay inside tokens ("x-men") — splitting on `-` dropped the
        // length-1 "x" and searched only "men" (#785 Bugbot Medium).
        return lower.Split(
            [' ', '\t', ',', '.', '!', '?', '(', ')', '/', '"', ':', ';'],
            StringSplitOptions.RemoveEmptyEntries);
    }
}
