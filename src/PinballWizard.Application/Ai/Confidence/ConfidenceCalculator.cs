namespace PinballWizard.Application.Ai.Confidence;

// Default IConfidenceCalculator implementation per ADR-0017 § Confidence
// calculation. Phase 3 fills the three signals with simple but defensible
// approximations; PR 7+ can plug in real logprobs from Foundry's
// gen_ai.* attributes without changing the public contract.
public sealed class ConfidenceCalculator : IConfidenceCalculator
{
    // Phase 3 placeholder for the model-self-reported signal. Set to a
    // moderate value (0.85) so the composite isn't artificially pinned
    // to 1.0 just because we're not yet measuring logprobs. A future PR
    // wires this from Foundry's response metadata; the calculator
    // contract doesn't change.
    private const double ModelSelfReportedPlaceholder = 0.85;

    public ConfidenceSignals Compute(string answerText, IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);

        var citationCount = citations.Count;

        // RetrievalSimilarity: Phase 3 has no real similarity score —
        // it's a binary "did the agent's tool calls return anything"
        // signal. 1.0 if at least one citation present; 0.5 otherwise
        // (per ADR-0017's Phase 3 stub semantics). Phase 4 RAG replaces
        // this with the actual cosine-similarity-of-top-retrieved score.
        var retrievalSimilarity = citationCount > 0 ? 1.0 : 0.5;

        // CitationCoverage: fraction of factual claims with at least
        // one citation. Without paragraph-level claim extraction (out
        // of Phase 3 scope), approximate as: 1.0 when answer contains
        // at least one citation marker, scaled by paragraph count when
        // multiple paragraphs but only one citation. The ADR-0017
        // floor of 0.05 (applied in ConfidenceSignals.Composite)
        // prevents zero-out on this approximation.
        var citationCoverage = ComputeCitationCoverage(answerText, citations);

        return new ConfidenceSignals(
            RetrievalSimilarity: retrievalSimilarity,
            ModelSelfReported: ModelSelfReportedPlaceholder,
            CitationCoverage: citationCoverage);
    }

    public RefusalCategory CategorizeRefusal(ConfidenceSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        // Pick the lowest signal — that's the "dominant deficit" that
        // best explains the below-threshold composite. Ties go to the
        // most operationally informative category.
        var r = signals.RetrievalSimilarity;
        var m = signals.ModelSelfReported;
        var c = signals.CitationCoverage;

        // No grounding at all (no machine matched, no tool result)
        // strongly suggests the question wasn't about something we
        // can answer — categorize OutOfScope. ADR-0017 distinguishes
        // OutOfScope from InsufficientGrounding by intent: out-of-scope
        // = "we can't answer this domain"; insufficient-grounding =
        // "we could answer this domain but our retrieval is degraded".
        // Phase 3 conflates the two on the no-citation path; PR 7+
        // can tease them apart once the eval-set surfaces the
        // distinction.
        if (c <= 0.05 && r <= 0.5)
        {
            return RefusalCategory.OutOfScope;
        }

        if (r <= c && r <= m)
        {
            return RefusalCategory.InsufficientGrounding;
        }

        if (m <= r && m <= c)
        {
            return RefusalCategory.LowModelConfidence;
        }

        // Citation coverage is the floor.
        return RefusalCategory.InsufficientGrounding;
    }

    // One tool-trace citation "covers" up to this many paragraphs.
    // Citations are answer-level artifacts extracted from tool traces —
    // they carry no paragraph attribution — so demanding one per
    // paragraph penalized exactly the well-grounded single-source answers
    // formatted for readability (observed live: ev-valuation-0001 c=0.17,
    // ev-repair-0001 c=0.20, both with perfect retrieval, both refused).
    // 4 is calibrated against the 2026-06-10 eval: a 6-paragraph
    // single-citation answer scores 0.5 (composite ≈ 0.75, answers) while
    // a 13+-paragraph single-citation sprawl still refuses. See
    // ADR-0017 follow-up entry 2026-06-10. Single tuning knob.
    private const double ParagraphsPerExpectedCitation = 4.0;

    private static double ComputeCitationCoverage(string answerText, IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return 0.0;
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            // The agent answered nothing. There's no claim to cover,
            // but there's also no factual claim to verify; treat as
            // zero coverage so the composite reflects the empty answer.
            return 0.0;
        }

        // Approximate paragraph count by double-newline blocks; fall
        // back to 1 if the answer is a single paragraph. The signal is
        // intentionally coarse — PR 7+ may refine with proper
        // claim-level extraction.
        var paragraphs = answerText
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        if (paragraphs <= 0)
        {
            paragraphs = 1;
        }

        // Saturating expectation: ceil(paragraphs / 4) citations is "full
        // coverage"; fewer is fractional. Zero-citation answers never reach
        // here, so the floor in ConfidenceSignals.Composite still kills them.
        var expected = Math.Ceiling(paragraphs / ParagraphsPerExpectedCitation);
        var coverage = citations.Count / expected;
        return coverage > 1.0 ? 1.0 : coverage;
    }
}
