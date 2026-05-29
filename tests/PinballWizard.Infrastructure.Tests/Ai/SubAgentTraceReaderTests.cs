using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

// Behavior tests for SubAgentTraceReader (Phase 4 W2-1, build-spec
// § Phase 4 scope item 11 / Phase 3 follow-up #4). The Wizard's
// connected-agents dispatch surfaces sub-agent invocations as
// FunctionCallContent inside the AgentResponse trace; this reader
// extracts the leaf sub-agent name so WizardAnswer.SubAgentUsed is
// no longer the always-Wizard placeholder Phase 3 H2 baseline measured
// at subagent_accuracy=0.033.
public sealed class SubAgentTraceReaderTests
{
    [Fact]
    public void Read_NullResponse_ReturnsWizard()
    {
        var result = SubAgentTraceReader.Read(null);
        Assert.Equal(AgentName.Wizard, result);
    }

    [Fact]
    public void Read_ResponseWithNoFunctionCalls_ReturnsWizard()
    {
        // Wizard answered directly — no sub-agent dispatch in the trace.
        // This is its own valid path (out-of-scope refusals,
        // Wizard.md-handled passport / metadata questions).
        var response = BuildResponse(
            new ChatMessage(ChatRole.Assistant, "Pinball machines have flippers and bumpers."));

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    [Fact]
    public void Read_ResponseWithOnlyGetMachineByTitleCall_ReturnsWizard()
    {
        // Wizard called the grounding tool but did not dispatch to any
        // sub-agent — the tool name doesn't match a sub-agent, so
        // SubAgentUsed stays Wizard.
        var response = BuildResponseWithFunctionCall("getMachineByTitle");

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    [Theory]
    [InlineData(AgentName.Valuation)]
    [InlineData(AgentName.Rules)]
    [InlineData(AgentName.Repair)]
    public void Read_SingleSubAgentCall_ReturnsThatSubAgent(string subAgentName)
    {
        var response = BuildResponseWithFunctionCall(subAgentName);

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(subAgentName, result);
    }

    [Fact]
    public void Read_MultipleSubAgentCalls_ReturnsLast()
    {
        // Multi-dispatch: "what's a Godzilla worth and what are its rules?"
        // emits both Valuation and Rules calls in sequence. v1 picks
        // the last; if multi-dispatch is common in eval, an ADR follow-up
        // considers a richer return shape.
        var response = BuildResponse(
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_v", AgentName.Valuation)]),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_r", AgentName.Rules)]));

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Rules, result);
    }

    [Fact]
    public void Read_GroundingToolCallBetweenSubAgentCalls_StillReturnsLastSubAgent()
    {
        // Realistic shape: Wizard → Valuation → getMachineByTitle (called
        // by Valuation's prompt) → Rules → final answer. The
        // getMachineByTitle call sits between the two sub-agents and
        // must NOT be confused for a sub-agent.
        var response = BuildResponse(
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_v", AgentName.Valuation)]),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_g", "getMachineByTitle")]),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_r", AgentName.Rules)]));

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Rules, result);
    }

    [Fact]
    public void Read_MultipleFunctionCallsInSingleMessage_AllConsidered()
    {
        // Some SDK call paths pack multiple FunctionCallContent into a
        // single ChatMessage.Contents list (one assistant message
        // emitting parallel tool calls). Inner foreach must visit all.
        var response = BuildResponse(new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call_v", AgentName.Valuation),
            new FunctionCallContent("call_r", AgentName.Repair),
        ]));

        var result = SubAgentTraceReader.Read(response);

        // Last in the contents list wins.
        Assert.Equal(AgentName.Repair, result);
    }

    [Fact]
    public void Read_FunctionCallNameUnknown_DoesNotMatch()
    {
        // Defensive: a future tool whose name happens to begin with one
        // of the agent names ("ValuationLookup") must not match. The
        // reader uses exact equality via HashSet<string> Ordinal.
        var response = BuildResponseWithFunctionCall("ValuationLookup");

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    [Fact]
    public void Read_CaseMismatchedSubAgentName_DoesNotMatch()
    {
        // The SDK preserves the function name we registered (AgentName.Valuation
        // = "Valuation"). A lowercased "valuation" should NOT match — match
        // happens via Ordinal, not OrdinalIgnoreCase. Pinning the case
        // sensitivity here so a future change is conscious.
        var response = BuildResponseWithFunctionCall("valuation");

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    [Fact]
    public void Read_NullMessagesCollection_GracefullyReturnsWizard()
    {
        // Pins the contract: null Messages or null Contents (which the
        // SDK doesn't currently emit but the reader docstring promises
        // to tolerate) must not bubble an NPE out through AiRouter.
        // RuntimeHelpers.GetUninitializedObject bypasses the ctor so we
        // get an AgentResponse instance whose Messages is null,
        // mirroring the malformed-shape the reader is hardened against.
        var response = (AgentResponse)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(AgentResponse));

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    [Fact]
    public void Read_NullContentsCollection_GracefullyReturnsWizard()
    {
        // Same posture as the null-Messages test, one level deeper:
        // a ChatMessage with a null Contents must not blow up the
        // inner foreach. Construct via uninitialized-object so we hit
        // the null branch the reader's null-coalesce handles.
        var message = (ChatMessage)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ChatMessage));
        var response = BuildResponse(message);

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    [Fact]
    public void Read_FunctionResultContentOnly_DoesNotMatch()
    {
        // Defensive: only FunctionCallContent (the LLM-issued call)
        // counts. A FunctionResultContent (the function's reply, no
        // Name property) appearing in isolation must not surface a
        // sub-agent name.
        var response = BuildResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_x", "some result")]));

        var result = SubAgentTraceReader.Read(response);

        Assert.Equal(AgentName.Wizard, result);
    }

    private static AgentResponse BuildResponse(params ChatMessage[] messages)
    {
        return new AgentResponse(messages);
    }

    private static AgentResponse BuildResponseWithFunctionCall(string functionName)
    {
        return BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent($"call_{functionName}", functionName)]));
    }
}
