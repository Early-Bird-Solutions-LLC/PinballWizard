namespace PinballWizard.Application.Ai.Confidence;

// Computes ConfidenceSignals + the dominant-deficit signal for
// refusal-category selection per ADR-0017. Phase 3's signals come from:
//   RetrievalSimilarity — 1.0 if the agent's answer cites at least one
//                         catalog record (function-tool result), 0.5
//                         otherwise (no grounding evidence). Phase 4
//                         RAG replaces this with real cosine similarity.
//   ModelSelfReported   — Phase 3 placeholder of 0.85; PR 7+ wires
//                         logprobs from Foundry's gen_ai.* attributes.
//                         The signal exists in the calculator's signature
//                         so PR 6 can compute the composite cleanly;
//                         a future PR plugs in the real measurement
//                         without changing the contract.
//   CitationCoverage    — fraction of paragraphs in the answer that
//                         contain a recognizable citation marker. With
//                         only OPDB grounding in Phase 3, the citation
//                         marker is the OPDB source URL.
public interface IConfidenceCalculator
{
    ConfidenceSignals Compute(
        string answerText,
        IReadOnlyList<Citation> citations);

    // Returns the category that best describes WHY the composite is
    // below threshold — looks at which signal dominated the floor.
    // Caller (AiRouter) only invokes this when composite < threshold.
    RefusalCategory CategorizeRefusal(ConfidenceSignals signals);
}
