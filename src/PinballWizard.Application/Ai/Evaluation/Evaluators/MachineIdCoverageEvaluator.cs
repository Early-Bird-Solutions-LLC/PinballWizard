using PinballWizard.Application.Ai.Tools;

namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: when an eval question names a specific machine
// (EvalQuestion.MachineId is set), every searchCorpus call in the tool-call
// trace must carry a non-null, non-empty machineId argument. A searchCorpus
// call without machineId on a machine-scoped question is a scope regression —
// the agent retrieved corpus-wide instead of machine-scoped, risking
// cross-machine citation bleed (issue #719 / reference_eval_harness_no_tool_trace).
//
// Score semantics:
//   1.0 — all searchCorpus calls in the trace had a non-empty machineId.
//   0.0 — at least one searchCorpus call was missing the machineId argument.
//   null (undefined) — one of the inapplicable cases:
//         • WizardAnswer.ToolCallTrace is null (cache hit or early-exit path,
//           no agent run occurred — we cannot observe the calls).
//         • The trace contains no searchCorpus calls (agent answered without
//           corpus retrieval, e.g. via getMachineByTitle only — a separate
//           concern from argument scope).
//
// Applicable only for non-refused answers on questions where MachineId is set.
// The harness controls applicability; Compute always receives a non-null trace.
// The class is a singleton; Compute is pure — no I/O, no shared state.
public sealed class MachineIdCoverageEvaluator
{
    public const string EvaluatorName = "machine_id_coverage";

    public double? Compute(IReadOnlyList<ToolCallRecord>? trace)
    {
        if (trace is null)
        {
            // Null = no trace available (cache hit, early-exit). Undefined — caller
            // should have filtered these out, but guard defensively.
            return null;
        }

        var searchCorpusCalls = new List<ToolCallRecord>(capacity: trace.Count);
        foreach (var record in trace)
        {
            if (string.Equals(record.ToolName, SearchCorpusTool.ToolTagValue, StringComparison.Ordinal))
            {
                searchCorpusCalls.Add(record);
            }
        }

        if (searchCorpusCalls.Count == 0)
        {
            // No searchCorpus calls — metric is undefined for this answer.
            return null;
        }

        foreach (var call in searchCorpusCalls)
        {
            // "machineId" matches the C# parameter name in SearchCorpusTool.SearchCorpusAsync;
            // the Microsoft Agent Framework's AIFunctionFactory.Create preserves parameter names
            // verbatim in the generated JSON-Schema, so the LLM sends and the SDK records the
            // key as "machineId".
            if (!call.Arguments.TryGetValue("machineId", out var val)
                || string.IsNullOrWhiteSpace(val))
            {
                return 0.0;
            }
        }

        return 1.0;
    }
}
