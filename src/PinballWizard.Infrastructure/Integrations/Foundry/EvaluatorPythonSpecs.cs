using PinballWizard.Application.Ai.Evaluation.Evaluators;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

// Canonical Python equivalents of the nine custom code-based evaluators
// per ADR-0016 (the original four + the AB#259 edition-aware additions:
// answered_all_editions and honest_substitution + the issue #532 addition:
// grounding_integrity + the issue #719 addition: machine_id_coverage). Foundry's evaluator
// runtime executes Python; the .NET
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
        yield return $"{evaluatorNamespace}.{AnsweredAllEditionsEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{HonestSubstitutionEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{GroundingIntegrityEvaluator.EvaluatorName}";
        yield return $"{evaluatorNamespace}.{MachineIdCoverageEvaluator.EvaluatorName}";
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

    // Three-state per the AB#259 metric-hygiene fix (2026-06-10):
    // refusal_required rows must refuse; acceptable_refusal-only rows
    // accept either behavior (score None → excluded from the aggregate,
    // mirroring the C# null); all other rows must answer. Mirrors
    // RefusalCorrectnessEvaluator.
    public const string RefusalCorrectnessPython = """
def evaluate(predicted_refusal, acceptable_refusal, refusal_required=False, **_):
    if bool(refusal_required):
        return {"score": 1.0 if bool(predicted_refusal) else 0.0}
    if bool(acceptable_refusal):
        return {"score": None}
    return {"score": 0.0 if bool(predicted_refusal) else 1.0}
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

    // R2 (AB#259): every required edition named in the text (WHOLE WORD,
    // \b…\b — mirrors EditionLabelMatcher; naked substring would match
    // "Pro" inside "appropriate") + at least one distinct citation per
    // edition. Mirrors AnsweredAllEditionsEvaluator.
    public const string AnsweredAllEditionsPython = """
import re

def _names_edition(text, edition):
    tokens = [t.strip() for t in (edition or "").split("/") if t.strip()]
    return any(re.search(r"\b" + re.escape(t) + r"\b", text, re.IGNORECASE) for t in tokens)

def evaluate(answer_text, predicted, required_editions, **_):
    reqs = list(required_editions or [])
    if not reqs:
        return {"score": 0.0}
    text = answer_text or ""
    for edition in reqs:
        if not _names_edition(text, edition):
            return {"score": 0.0}
    distinct = len(set(s.lower() for s in (predicted or [])))
    return {"score": 1.0 if distinct >= len(reqs) else 0.0}
""";

    // R3 (AB#259): disclose the named edition's gap + cite a substitute.
    // The named-edition check is WHOLE WORD (\b…\b — mirrors
    // EditionLabelMatcher; naked substring would match "LE" inside
    // "available", letting the disclosure phrase defeat the guard) AND splits
    // a slash-separated label ("Premium/LE") into sub-tokens first, matching
    // if ANY sub-token is named — identical to EditionLabelMatcher.AnswerNamesEdition,
    // which both R2 and R3 use. (Without the split, a named_edition of
    // "Premium/LE" would require the literal substring "Premium/LE".)
    // Mirrors HonestSubstitutionEvaluator. NOTE: C# throws on a blank
    // named_edition (the harness guards it upstream); the Foundry runtime
    // has no upstream guard, so this returns 0.0 — behaviorally equivalent.
    // Grounding-integrity (issue #532): Rules/Repair answers must carry ≥1
    // CorpusChunk citation — not only a MachineRecord. None on refusals and
    // non-Rules/Repair rows (metric undefined). Mirrors
    // GroundingIntegrityEvaluator; the harness handles the conditional in
    // C# but the Python spec must be self-contained for Foundry registration.
    public const string GroundingIntegrityPython = """
def evaluate(citations, predicted_sub_agent, predicted_refusal=False, **_):
    if bool(predicted_refusal):
        return {"score": None}
    sub = (predicted_sub_agent or "").strip().lower()
    if sub not in ("rules", "repair"):
        return {"score": None}
    for c in (citations or []):
        if (c.get("source_type") or "") == "CorpusChunk":
            return {"score": 1.0}
    return {"score": 0.0}
""";

    // MachineIdCoverage (issue #719): every searchCorpus call must carry a
    // non-null machineId when the question names a machine. Mirrors
    // MachineIdCoverageEvaluator; null when trace is absent or has no
    // searchCorpus calls (metric undefined for those rows).
    public const string MachineIdCoveragePython = """
def evaluate(tool_call_trace, predicted_refusal=False, **_):
    if bool(predicted_refusal):
        return {"score": None}
    trace = tool_call_trace or []
    sc_calls = [c for c in trace if (c.get("tool_name") or "") == "searchCorpus"]
    if not sc_calls:
        return {"score": None}
    for call in sc_calls:
        mid = (call.get("arguments") or {}).get("machineId")
        if not mid or not str(mid).strip():
            return {"score": 0.0}
    return {"score": 1.0}
""";

    public const string HonestSubstitutionPython = """
import re

def _names_edition(text, edition):
    tokens = [t.strip() for t in (edition or "").split("/") if t.strip()]
    return any(re.search(r"\b" + re.escape(t) + r"\b", text, re.IGNORECASE) for t in tokens)

def evaluate(answer_text, predicted, named_edition, **_):
    cites = list(predicted or [])
    if not cites:
        return {"score": 0.0}
    edition = (named_edition or "").strip()
    if not edition:
        return {"score": 0.0}
    text = answer_text or ""
    if not _names_edition(text, edition):
        return {"score": 0.0}
    phrases = ["don't have", "dont have", "do not have", "isn't available",
               "isnt available", "is not available", "aren't available",
               "are not available", "not available", "unavailable", "no specific"]
    low = text.lower()
    return {"score": 1.0 if any(p in low for p in phrases) else 0.0}
""";
}
