namespace PinballWizard.Application.Ai;

// Decides whether a no-retrieval follow-up may inherit citations from a
// prior turn (ADR-0026 multi-turn contract + #780).
//
// Clarifying follow-ups ("how many balls in multiball?") answer from
// conversation context and legitimately share the prior turn's grounding.
// New-intent follow-ups ("show me games similar to Medieval Madness") ask
// a different question that a cold ask would have to ground on its own —
// borrowing manuals/rules citations to pass the confidence gate is the
// hole #780 measured. Inheritance stays off for those.
public static class FollowUpCitationInheritance
{
    // Multi-word phrases matched as case-insensitive substrings against the
    // whitespace-collapsed follow-up. Keep the list small and explicit —
    // false negatives (blocking inheritance for a true clarification) are
    // recoverable (model re-retrieves); false positives reopen the #780 hole.
    private static readonly string[] NewIntentPhrases =
    [
        "similar to",
        "similar games",
        "games like",
        "machines like",
        "games similar",
        "recommend",
        "recommendation",
        "other games",
        "other machines",
        "alternatives",
        "what else",
        "show me games",
        "list games",
        "list machines",
        "more games like",
        "more machines like",
    ];

    // Single-token markers matched as whole words (after stripping trailing
    // sentence punctuation). Space-padded substrings miss sentence-edge and
    // "?"-attached forms — e.g. "How do they compare?" / "vs Attack from Mars"
    // (#780 Bugbot Medium).
    private static readonly HashSet<string> NewIntentWords = new(StringComparer.Ordinal)
    {
        "compare",
        "compared",
        "vs",
        "versus",
    };

    public static bool ShouldInherit(string followUpQuestion, IReadOnlyList<ConversationTurn> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(followUpQuestion))
        {
            return false;
        }

        // Collapse whitespace so multi-space variants still match phrases.
        var normalized = string.Join(
            ' ',
            followUpQuestion.Trim().ToLowerInvariant().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

        foreach (var phrase in NewIntentPhrases)
        {
            if (normalized.Contains(phrase, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var token in normalized.Split(' '))
        {
            var bare = token.TrimEnd('?', '.', '!', ',', ';', ':');
            if (bare.Length > 0 && NewIntentWords.Contains(bare))
            {
                return false;
            }
        }

        return true;
    }
}
