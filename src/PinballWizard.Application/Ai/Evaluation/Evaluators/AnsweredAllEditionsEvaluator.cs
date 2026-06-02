namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator for R2 (AB#259, edition-scope-model-design
// §5/§6): when an answer should DIFFER by edition, the Wizard's locked
// behavior is ONE response that attributes each edition (no clarifying
// round-trip). This evaluator scores that intended shape:
//
//   1.0 when the answer text names EVERY required edition (attribution)
//       AND the predicted citation set has at least as many distinct
//       citations as there are required editions (a source per edition).
//   0.0 otherwise.
//
// Attribution heuristic (deliberately simple + documented per the task):
//   - Each required-edition label (e.g. "Pro", "Premium/LE") must appear
//     in the answer text, case-insensitively.
//   - A slash-separated label ("Premium/LE") matches if ANY of its
//     sub-tokens ("Premium" OR "LE") appears — the slash denotes
//     alternative spellings of one edition base, so naming either form
//     attributes that base.
//
// Citation-per-edition heuristic:
//   - We cannot map a specific citation id to a specific edition without
//     Citation carrying edition_scope (a known limitation — see
//     CitationPrecisionEvaluator.ComputeAnyOf). As a proxy we require the
//     distinct predicted-citation count to be >= the required-edition
//     count, so an answer attributing two editions but citing only one
//     source fails (it cannot have a distinct source per edition).
//
// The class is a singleton; Compute is pure — no I/O, no shared state.
public sealed class AnsweredAllEditionsEvaluator
{
    public const string EvaluatorName = "answered_all_editions";

    public double Compute(
        string answerText,
        IReadOnlyCollection<string> predictedCitationIds,
        IReadOnlyList<string> requiredEditions)
    {
        ArgumentNullException.ThrowIfNull(answerText);
        ArgumentNullException.ThrowIfNull(predictedCitationIds);
        ArgumentNullException.ThrowIfNull(requiredEditions);

        if (requiredEditions.Count == 0)
        {
            // Misconfigured row: answered_all_editions with nothing to
            // attribute. No ground truth to satisfy → fail loudly so the
            // curator notices.
            return 0.0;
        }

        foreach (var edition in requiredEditions)
        {
            // Whole-word match (EditionLabelMatcher) — naked Contains would
            // false-positive on "Pro" inside "appropriate"/"process".
            if (!EditionLabelMatcher.AnswerNamesEdition(answerText, edition))
            {
                return 0.0;
            }
        }

        var distinctCitations = new HashSet<string>(
            predictedCitationIds, StringComparer.OrdinalIgnoreCase).Count;
        if (distinctCitations < requiredEditions.Count)
        {
            return 0.0;
        }

        return 1.0;
    }
}
