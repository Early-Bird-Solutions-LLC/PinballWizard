namespace PinballWizard.Application.Findability;

// Prepares free-text typeahead queries for the machine suggest index (#785).
//
// The landing ask-box sends the *entire question* to suggest ("whats a good
// strategy for transformers"). Without normalization, AI Search BM25 matches
// stopwords and question fluff against unrelated titles (Fore ← "for",
// No Good Gofers ← "good", Invasion Strategy ← "strategy").
//
// Two stages:
//   1. Tokenize + strip stopwords → search text sent to the index.
//   2. For multi-token (question-shaped) queries, post-filter hits so a title
//      must share a *max-length* significant token with the query. That drops
//      Invasion Strategy when "transformers" is the distinctive word, while
//      still allowing a bare name query "strategy" → Invasion Strategy
//      (single-token path: any shared token).
//
// Stopword list mirrors MachineGroundingTool.TokenStopWords (canonical
// machine-title vocabulary) plus typeahead-specific question contractions
// and filler ("whats", "good", "best"). Kept here rather than reaching into
// the tool's `internal` set so Findability stays free of Ai.Tools coupling.
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

    // Tokenizes free text for suggest: lowercase, split on whitespace /
    // punctuation, drop single-char tokens and stopwords. Numerics kept
    // (007, 2001) — same posture as MachineGroundingTool.TokenizeForOverlap.
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

        return result;
    }

    // Search text sent to AI Search. Empty when every token was a stopword —
    // caller should return no suggestions (honest empty, no I/O).
    public static string BuildSearchText(IReadOnlyList<string> tokens)
        => tokens.Count == 0 ? string.Empty : string.Join(' ', tokens);

    // True when a hit title is on-topic for the normalized query tokens.
    //
    // Single-token queries (name typing): any shared significant token.
    // Multi-token queries (question-shaped): the title must share a token of
    // *maximum* length among the query tokens — the distinctive entity word
    // ("transformers"), not shorter leftovers that survived stopword stripping
    // (e.g. "strategy" ranking Invasion Strategy).
    public static bool TitleMatchesQuery(string title, IReadOnlyList<string> queryTokens)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(title))
            return false;

        var titleTokens = Tokenize(title);
        if (titleTokens.Count == 0)
            return false;

        var titleSet = new HashSet<string>(titleTokens, StringComparer.Ordinal);

        if (queryTokens.Count == 1)
            return titleSet.Contains(queryTokens[0]);

        var maxLen = 0;
        for (var i = 0; i < queryTokens.Count; i++)
        {
            if (queryTokens[i].Length > maxLen)
                maxLen = queryTokens[i].Length;
        }

        for (var i = 0; i < queryTokens.Count; i++)
        {
            if (queryTokens[i].Length == maxLen && titleSet.Contains(queryTokens[i]))
                return true;
        }

        return false;
    }
}
