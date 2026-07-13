using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PinballWizard.Application.Ai;

// Reads the tool-call sequence from an AgentResponse trace and converts
// it to a flat list of ToolCallRecord values for eval and observability.
// Parallel to SubAgentTraceReader — same null-tolerance contract: null
// or malformed response returns an empty list rather than throwing, so
// the caller (ApplyPostAgentGuardrailsAsync) cannot be taken down by a
// bad agent response shape (invariant #17: degrade visibly, never NPE).
//
// Each FunctionCallContent entry in the agent's ChatMessages becomes one
// ToolCallRecord. FunctionResultContent (the tool's reply, no Name) and
// TextContent entries are ignored — only the LLM-issued call side is
// relevant for argument-level evaluation.
//
// Argument values are normalized to string? at read time so evaluators
// and tests never handle JsonElement vs string vs int:
//   - string  → kept as-is
//   - JsonElement(String) → GetString() (raw string value, no quotes)
//   - JsonElement(Null) → null
//   - any other JsonElement → ToString() (JSON-text representation)
//   - null  → null
//   - other → value.ToString()
public static class ToolCallTraceReader
{
    public static IReadOnlyList<ToolCallRecord> Read(AgentResponse? response)
    {
        if (response is null)
        {
            return [];
        }

        List<ToolCallRecord>? records = null;

        foreach (var message in response.Messages ?? [])
        {
            foreach (var content in message.Contents ?? [])
            {
                if (content is not FunctionCallContent call)
                {
                    continue;
                }

                var args = NormalizeArguments(call.Arguments);
                records ??= [];
                records.Add(new ToolCallRecord(call.Name ?? string.Empty, args));
            }
        }

        return records is null ? [] : records;
    }

    private static Dictionary<string, string?> NormalizeArguments(
        IDictionary<string, object?>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return new Dictionary<string, string?>();
        }

        var result = new Dictionary<string, string?>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            result[key] = ToArgString(value);
        }

        return result;
    }

    private static string? ToArgString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement je => je.ToString(),
        _ => value.ToString(),
    };
}
