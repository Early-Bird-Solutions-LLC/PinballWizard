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
    // Phrases that mark a standalone discovery / recommendation ask.
    // Matched as case-insensitive substring against the normalized follow-up.
    // Keep the list small and explicit — false negatives (blocking inheritance
    // for a true clarification) are recoverable (model re-retrieves); false
    // positives reopen the #780 hole.
    private static readonly string[] NewIntentMarkers =
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
        "compare ",
        " compared",
        " vs ",
        " vs.",
        " versus ",
        "what else",
        "show me games",
        "list games",
        "list machines",
        "more games like",
        "more machines like",
    ];

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

        // Collapse whitespace so "vs." / multi-space variants still match.
        var normalized = string.Join(
            ' ',
            followUpQuestion.Trim().ToLowerInvariant().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

        foreach (var marker in NewIntentMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
