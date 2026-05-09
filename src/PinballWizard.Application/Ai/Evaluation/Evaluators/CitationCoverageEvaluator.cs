namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: fraction of an answer's paragraphs that
// have at least one citation backing them. The fourth eval surface,
// added in Phase 4 W4-2 per build-spec § Phase 4 scope item 22.
//
// Mirrors the heuristic in
// `PinballWizard.Application.Ai.Confidence.ConfidenceCalculator.ComputeCitationCoverage`
// — the eval surface is the *measurement* layer for the same signal the
// confidence calculator already consults at request time. Keeping the
// two implementations aligned is a deliberate v1 choice: a divergence
// would mean the user-visible refusal posture (driven by the confidence
// signal) and the eval-baseline-reported posture told different stories
// at H3 calibration time, which would confuse threshold movement (per
// ADR-0017 / ADR-0023). A future Phase 4.5 / W4-2.5 PR can lift the
// evaluator to a semantic-similarity-via-embedding implementation per
// ADR-0022's eventual coverage definition; the registration surface
// (Foundry-side Python spec in `EvaluatorPythonSpecs`) carries the
// upgrade in lockstep.
//
// Conventions:
//   - Coarse paragraph-fraction (1 citation per paragraph = full
//     coverage; multiple citations don't compound past 1.0).
//   - Empty answer with no citations: 0.0 (no claim covered, no
//     citation present — the composite reflects the empty answer).
//   - Non-empty answer with no citations: 0.0 (the user-visible promise
//     is *every answer cites a source* per ADR-0023; an uncited answer
//     hits the citation-required gate and is a refusal at the router
//     layer — but at eval time the un-refused result still scores 0).
//   - Whitespace-only answer with citations: 0.0 (degenerate).
//
// The class is a singleton; Compute is pure — no I/O, no shared state.
// Foundry registers this evaluator with a Python equivalent for portal
// surface alignment (see EvaluatorPythonSpecs.CitationCoveragePython).
public sealed class CitationCoverageEvaluator
{
    public const string EvaluatorName = "citation_coverage";

    public double Compute(string answerText, IReadOnlyCollection<string> predictedCitationIds)
    {
        ArgumentNullException.ThrowIfNull(predictedCitationIds);

        if (predictedCitationIds.Count == 0)
        {
            return 0.0;
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            return 0.0;
        }

        // Paragraph count via double-newline separation; matches the
        // ConfidenceCalculator heuristic so divergence surfaces as a
        // sibling-diff finding in the PR self-audit if either side is
        // edited without the other.
        var paragraphs = answerText
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        if (paragraphs <= 0)
        {
            paragraphs = 1;
        }

        var coverage = (double)predictedCitationIds.Count / paragraphs;
        return coverage > 1.0 ? 1.0 : coverage;
    }
}
