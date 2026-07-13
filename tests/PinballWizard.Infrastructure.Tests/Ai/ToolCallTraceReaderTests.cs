using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

// Behavior tests for ToolCallTraceReader (issue #719). The eval harness
// consumes the trace via WizardAnswer.ToolCallTrace; this reader is the
// source that populates it from the AgentResponse. Mirrors the null-
// tolerance contract of SubAgentTraceReader.
public sealed class ToolCallTraceReaderTests
{
    [Fact]
    public void Read_NullResponse_ReturnsEmpty()
    {
        var result = ToolCallTraceReader.Read(null);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_ResponseWithNoFunctionCalls_ReturnsEmpty()
    {
        var response = BuildResponse(
            new ChatMessage(ChatRole.Assistant, "Pinball machines have flippers."));

        var result = ToolCallTraceReader.Read(response);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_OneFunctionCall_ReturnsOneRecord()
    {
        var args = new Dictionary<string, object?> { ["query"] = "wizard mode" };
        var response = BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_1", "searchCorpus", args)]));

        var result = ToolCallTraceReader.Read(response);

        var record = Assert.Single(result);
        Assert.Equal("searchCorpus", record.ToolName);
        Assert.Equal("wizard mode", record.Arguments["query"]);
    }

    [Fact]
    public void Read_FunctionCallWithMachineId_RetainsMachineId()
    {
        // The specific regression issue #719 guards: a searchCorpus call
        // with machineId must surface that argument so the evaluator can
        // assert it was present.
        var args = new Dictionary<string, object?>
        {
            ["query"] = "multiball rules",
            ["machineId"] = "GRBN-MQR4P",
        };
        var response = BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_sc", "searchCorpus", args)]));

        var result = ToolCallTraceReader.Read(response);

        var record = Assert.Single(result);
        Assert.Equal("GRBN-MQR4P", record.Arguments["machineId"]);
    }

    [Fact]
    public void Read_FunctionCallWithNullMachineId_RecordsNull()
    {
        // LLM explicitly passed null for machineId — the regression pattern
        // issue #719 is designed to catch. Null must be preserved (not
        // converted to empty string) so the evaluator can distinguish
        // "omitted" vs "null".
        var args = new Dictionary<string, object?> { ["query"] = "flipper gap", ["machineId"] = null };
        var response = BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_sc", "searchCorpus", args)]));

        var result = ToolCallTraceReader.Read(response);

        var record = Assert.Single(result);
        Assert.True(record.Arguments.ContainsKey("machineId"));
        Assert.Null(record.Arguments["machineId"]);
    }

    [Fact]
    public void Read_JsonElementStringValue_NormalizesToString()
    {
        // The Foundry SDK may produce JsonElement values in the arguments
        // dict (args parsed from the LLM's JSON payload). ToolCallTraceReader
        // must normalize them to plain strings so the evaluator never handles
        // JsonElement.
        var je = JsonDocument.Parse("\"GRBN-MQR4P\"").RootElement;
        var args = new Dictionary<string, object?> { ["machineId"] = je };
        var response = BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_sc", "searchCorpus", args)]));

        var result = ToolCallTraceReader.Read(response);

        var record = Assert.Single(result);
        // After normalization the value is a plain string, not a JsonElement.
        Assert.Equal("GRBN-MQR4P", record.Arguments["machineId"]);
    }

    [Fact]
    public void Read_JsonElementNullValue_NormalizesToNull()
    {
        var je = JsonDocument.Parse("null").RootElement;
        var args = new Dictionary<string, object?> { ["machineId"] = je };
        var response = BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_sc", "searchCorpus", args)]));

        var result = ToolCallTraceReader.Read(response);

        var record = Assert.Single(result);
        Assert.Null(record.Arguments["machineId"]);
    }

    [Fact]
    public void Read_MultipleFunctionCallsAcrossMessages_ReturnsAllInOrder()
    {
        // Multiple tool calls across separate messages — all captured, order
        // preserved so the evaluator can reason about the call sequence.
        var response = BuildResponse(
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call_g", "getMachineByTitle",
                    new Dictionary<string, object?> { ["title"] = "Godzilla" })]),
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call_s", "searchCorpus",
                    new Dictionary<string, object?> { ["query"] = "rules", ["machineId"] = "GweeP-MW95j" })]));

        var result = ToolCallTraceReader.Read(response);

        Assert.Equal(2, result.Count);
        Assert.Equal("getMachineByTitle", result[0].ToolName);
        Assert.Equal("searchCorpus", result[1].ToolName);
        Assert.Equal("GweeP-MW95j", result[1].Arguments["machineId"]);
    }

    [Fact]
    public void Read_MultipleFunctionCallsInSingleMessage_ReturnsAll()
    {
        // Parallel tool calls packed in one ChatMessage.Contents list.
        var response = BuildResponse(new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call_a", "getMachineByTitle",
                new Dictionary<string, object?> { ["title"] = "Godzilla" }),
            new FunctionCallContent("call_b", "searchCorpus",
                new Dictionary<string, object?> { ["query"] = "rules", ["machineId"] = "GweeP-MW95j" }),
        ]));

        var result = ToolCallTraceReader.Read(response);

        Assert.Equal(2, result.Count);
        Assert.Equal("getMachineByTitle", result[0].ToolName);
        Assert.Equal("searchCorpus", result[1].ToolName);
    }

    [Fact]
    public void Read_FunctionResultContent_IsIgnored()
    {
        // FunctionResultContent (the tool's reply) must NOT appear in the
        // trace — only the LLM-issued call side is relevant.
        var response = BuildResponse(
            new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("call_sc", "some result")]));

        var result = ToolCallTraceReader.Read(response);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_NullMessagesCollection_ReturnsEmpty()
    {
        // Null Messages (malformed response) must not throw — mirrors the
        // null-tolerance contract of SubAgentTraceReader.
        var response = (AgentResponse)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(AgentResponse));

        var result = ToolCallTraceReader.Read(response);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_NullContentsCollection_ReturnsEmpty()
    {
        // Null Contents on a ChatMessage — should not throw.
        var message = (ChatMessage)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ChatMessage));
        var response = BuildResponse(message);

        var result = ToolCallTraceReader.Read(response);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_NullArguments_RecordsEmptyDictionary()
    {
        // FunctionCallContent with null Arguments (LLM omitted the args block
        // entirely) must not throw; the record's Arguments is an empty dict.
        var response = BuildResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_x", "searchCorpus", arguments: null)]));

        var result = ToolCallTraceReader.Read(response);

        var record = Assert.Single(result);
        Assert.Empty(record.Arguments);
    }

    private static AgentResponse BuildResponse(params ChatMessage[] messages)
        => new AgentResponse(messages);
}
