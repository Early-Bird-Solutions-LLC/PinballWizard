namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator for R3 (AB#259, edition-scope-model-design
// §5/§6): the user named an edition the corpus has no data for. The
// Wizard's locked behavior is HONEST SUBSTITUTION — disclose that the
// named edition's data is absent, then cite the substitute. Two failure
// modes this guards against: silent substitution (answering from the
// wrong edition without saying so) and blanket refusal (citing nothing).
//
//   1.0 when the answer DISCLOSES the named-edition gap (a disclosure
//       phrase referencing the edition appears in the text) AND cites a
//       substitute (predicted citation set is non-empty).
//   0.0 otherwise.
//
// Disclosure heuristic (deliberately simple + documented per the task):
//   - The answer contains one of a small set of disclosure phrases
//     ("don't have", "do not have", "isn't available", "is not available",
//     "no ... -specific", "unavailable") AND the named edition label
//     appears in the text. Both must hold so a generic "I don't have that"
//     about something else doesn't accidentally pass, and so naming the
//     edition without disclosing (silent substitution) fails.
//
// The class is a singleton; Compute is pure — no I/O, no shared state.
public sealed class HonestSubstitutionEvaluator
{
    public const string EvaluatorName = "honest_substitution";

    private static readonly string[] DisclosurePhrases =
    [
        "don't have",
        "dont have",
        "do not have",
        "isn't available",
        "isnt available",
        "is not available",
        "aren't available",
        "are not available",
        "not available",
        "unavailable",
        "no specific",
    ];

    public double Compute(
        string answerText,
        IReadOnlyCollection<string> predictedCitationIds,
        string namedEdition)
    {
        ArgumentNullException.ThrowIfNull(answerText);
        ArgumentNullException.ThrowIfNull(predictedCitationIds);
        // The harness owns the empty-namedEdition guard (it scores 0.0
        // without calling Compute when the row supplies no required edition),
        // so reaching here with a blank label is a programming error, not a
        // data case — throw. The Python mirror returns 0.0 for the blank
        // case because Foundry's runtime passes the row's field directly with
        // no upstream guard; the two stay behaviorally equivalent end-to-end.
        ArgumentException.ThrowIfNullOrWhiteSpace(namedEdition);

        // Blanket refusal: nothing cited → fail (the user-visible promise
        // is honest substitution, not refusal).
        if (predictedCitationIds.Count == 0)
        {
            return 0.0;
        }

        // The named edition must appear in the text as a WHOLE WORD —
        // otherwise the answer cannot have disclosed THAT edition's gap.
        // Naked Contains would false-positive on short labels: "LE" ⊂
        // "available", so "that data is not available" (a disclosure phrase
        // that itself contains "le") would defeat this guard. See
        // EditionLabelMatcher.
        if (!EditionLabelMatcher.AnswerNamesEdition(answerText, namedEdition))
        {
            return 0.0;
        }

        foreach (var phrase in DisclosurePhrases)
        {
            if (answerText.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }
        }

        // Named the edition + cited a substitute, but never disclosed the
        // gap → silent substitution → fail.
        return 0.0;
    }
}
