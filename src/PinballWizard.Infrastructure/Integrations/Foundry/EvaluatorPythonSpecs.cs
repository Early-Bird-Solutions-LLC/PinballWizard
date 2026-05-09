using PinballWizard.Application.Ai.Evaluation.Evaluators;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

// Canonical Python equivalents of the four custom code-based evaluators
// per ADR-0016. Foundry's evaluator runtime executes Python; the .NET
// classes in PinballWizard.Application.Ai.Evaluation.Evaluators are the
// in-process Phase 3 implementation; these snippets are the spec for
// the future Foundry-side registration when Azure.AI.Projects exposes
// CodeBasedEvaluatorDefinition.CreateVersionAsync as public API.
//
// Drift discipline: any change to the corresponding .NET evaluator's
// Compute logic must change this file too (the sibling-diff item of
// the 7-item PR self-audit catches this). Keep the snippets short and
// behaviorally aligned with the C# methods.
internal static class EvaluatorPythonSpecs
{
    public static IEnumerable<string> AllNames(string evaluatorNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorNamespace);
        yield return $"{evaluatorNamespace}.{CitationPrecisionEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{CitationRecallEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{CitationCoverageEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{SubagentAccuracyEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{RefusalCorrectnessEvaluator.EvaluatorName}";
    }

    public const string CitationPrecisionPython = """
def evaluate(predicted, expected, **_):
    pset = set(s.lower() for s in (predicted or []))
    eset = set(s.lower() for s in (expected or []))
    if not pset:
        return {"score": 1.0 if not eset else 0.0}
    hits = sum(1 for p in pset if p in eset)
    return {"score": hits / len(pset)}
""";

    public const string CitationRecallPython = """
def evaluate(predicted, expected, **_):
    pset = set(s.lower() for s in (predicted or []))
    eset = set(s.lower() for s in (expected or []))
    if not eset:
        return {"score": 1.0}
    hits = sum(1 for e in eset if e in pset)
    return {"score": hits / len(eset)}
""";

    public const string SubagentAccuracyPython = """
def evaluate(predicted_sub_agent, expected_sub_agent, **_):
    a = (predicted_sub_agent or "").strip().lower()
    b = (expected_sub_agent or "").strip().lower()
    return {"score": 1.0 if a == b else 0.0}
""";

    public const string RefusalCorrectnessPython = """
def evaluate(predicted_refusal, acceptable_refusal, **_):
    return {"score": 1.0 if bool(predicted_refusal) == bool(acceptable_refusal) else 0.0}
""";

    public const string CitationCoveragePython = """
def evaluate(answer_text, predicted, **_):
    cites = list(predicted or [])
    if not cites:
        return {"score": 0.0}
    text = (answer_text or "").strip()
    if not text:
        return {"score": 0.0}
    paragraphs = [p for p in text.replace("\r\n", "\n").split("\n\n") if p]
    n = max(len(paragraphs), 1)
    coverage = len(cites) / n
    return {"score": min(coverage, 1.0)}
""";
}
