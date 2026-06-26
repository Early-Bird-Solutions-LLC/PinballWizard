using PinballWizard.Application.Ai;

namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: for Rules and Repair sub-agent answers, at
// least one citation must be a corpus chunk (a searchCorpus hit). A
// getMachineByTitle identity record (MachineRecord / OPDB entry) confirms
// the machine exists but is NOT a source citation for gameplay or repair
// content — an answer backed only by the machine record and no corpus
// chunks is parametric, not grounded.
//
// This evaluator catches the citation-provenance gap described in issue
// #532: even when the Wizard grounds the correct machine, an answer that
// cites only the OPDB record (no corpus hits) is effectively ungrounded
// for gameplay and repair questions.
//
// Applicable only for non-refused Rules/Repair answers. The harness
// controls applicability: it only calls Compute when predictedSubAgent is
// "Rules" or "Repair" AND the answer is not a refusal. For all other rows
// (Valuation, refusals, error rows) the harness leaves the score null —
// excluded from the aggregate denominator to avoid diluting the signal.
//
// The class is a singleton; Compute is pure — no I/O, no shared state.
// Foundry registers this evaluator with a Python equivalent for portal
// surface alignment (see EvaluatorPythonSpecs.GroundingIntegrityPython).
public sealed class GroundingIntegrityEvaluator
{
    public const string EvaluatorName = "grounding_integrity";

    public double Compute(IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);

        return citations.Any(c => c.SourceType == CitationSourceType.CorpusChunk) ? 1.0 : 0.0;
    }
}
