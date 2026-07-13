namespace PinballWizard.Application.Ai;

// One tool-call invocation from the Wizard agent's execution trace.
// Captured by ToolCallTraceReader from AgentResponse.Messages so eval
// can assert on tool arguments directly — the gap that made a
// machineId-drop regression only detectable via collateral citation-
// precision collapse (reference_eval_harness_no_tool_trace, issue #719).
//
// Arguments is a flat string map: values are normalized to strings at
// read time (ToolCallTraceReader) so consumers — evaluators, tests, log
// sinks — never have to distinguish JsonElement from string from int.
// Null entry means the LLM explicitly passed null for that argument
// (distinct from the key being absent, which means the argument was
// omitted entirely).
//
// ToolName equals the JSON-Schema function name the Microsoft Agent
// Framework derived from the tool method — "searchCorpus" for
// SearchCorpusTool and "getMachineByTitle" for MachineGroundingTool.
// SearchCorpusTool.ToolTagValue is the canonical constant.
public sealed record ToolCallRecord(
    string ToolName,
    IReadOnlyDictionary<string, string?> Arguments);
