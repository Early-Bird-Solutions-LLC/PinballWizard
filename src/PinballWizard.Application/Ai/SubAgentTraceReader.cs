using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PinballWizard.Application.Ai;

// Reads the "which sub-agent answered this question" signal from the
// AgentResponse trace produced by the Wizard's connected-agents
// dispatch. Replaces the Phase 3 PR 4 placeholder (`SubAgentUsed`
// always = "Wizard") that left the eval surface blind to whether
// routing had actually engaged.
//
// Background: per W1-1 (build-spec § Phase 4 scope item 8) the Wizard
// gets each sub-agent (Valuation / Rules / Repair) wrapped via
// AIAgent.AsAIFunction(); when the LLM routes a question, it issues
// a function call whose Name equals the sub-agent's AgentName. The
// Microsoft Agent Framework dispatches the call, weaves the reply
// into the conversation, and the function call shows up in
// response.Messages as a FunctionCallContent.
//
// Heuristic: the LAST sub-agent function call in the trace wins.
// Multi-dispatch ("what's it worth AND what are the rules?") can emit
// two function calls; v1 picks the last so SubAgentUsed reflects the
// final lift. The handful of multi-dispatch cases in eval will surface
// at H3 baseline calibration; if multi-dispatch is common, ADR-0024-
// style follow-up considers a richer return shape (set-of-sub-agents
// instead of single string). v1 is intentionally minimal.
//
// Returns AgentName.Wizard when no sub-agent function call appears in
// the trace — the Wizard answered directly without delegating, which
// is its own valid answer-path (e.g., out-of-scope refusals, simple
// passport / metadata questions Wizard.md handles inline).
public static class SubAgentTraceReader
{
    // The set of names that count as sub-agents (i.e., not the Wizard
    // itself). Pinned to AgentName.All minus Wizard so adding a new
    // sub-agent in a future ADR (e.g., Phase 5+ "Strategy" passport
    // module sub-agent) automatically lights up this reader.
    private static readonly HashSet<string> SubAgentNames =
        AgentName.All.Where(n => n != AgentName.Wizard).ToHashSet(StringComparer.Ordinal);

    public static string Read(AgentResponse? response)
    {
        if (response is null)
        {
            return AgentName.Wizard;
        }

        string? lastSubAgent = null;

        // Null-coalesce both collections: Microsoft.Agents.AI 1.4.0
        // returns non-null in practice, but the reader's contract above
        // promises graceful degradation to AgentName.Wizard rather than
        // an NPE that would bubble out of AiRouter (the call site sits
        // outside the wizard.RunAsync try/catch). Sibling
        // ToolTraceCitationExtractor was hardened symmetrically in the
        // same PR.
        foreach (var message in response.Messages ?? Array.Empty<ChatMessage>())
        {
            foreach (var content in message.Contents ?? Array.Empty<AIContent>())
            {
                if (content is not FunctionCallContent call)
                {
                    continue;
                }

                if (SubAgentNames.Contains(call.Name))
                {
                    lastSubAgent = call.Name;
                }
            }
        }

        return lastSubAgent ?? AgentName.Wizard;
    }
}
