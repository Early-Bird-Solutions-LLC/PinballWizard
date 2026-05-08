using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai.Citations;

public sealed class ToolTraceCitationExtractorTests
{
    private static readonly ToolTraceCitationExtractor Extractor = new();

    [Fact]
    public void SourceTag_IsToolTrace()
    {
        Assert.Equal("tool_trace", Extractor.SourceTag);
    }

    [Fact]
    public void Extract_NullResponse_ReturnsEmpty()
    {
        var citations = Extractor.Extract(null);
        Assert.Empty(citations);
    }

    [Fact]
    public void Extract_NoFunctionResults_ReturnsEmpty()
    {
        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Assistant, "Godzilla is a Stern pinball machine from 2021."));

        var citations = Extractor.Extract(response);

        Assert.Empty(citations);
    }

    [Fact]
    public void Extract_GetMachineByTitleResult_ProducesStructuredCitation()
    {
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var response = BuildAgentResponseWithToolResult(
            functionName: "getMachineByTitle",
            result: dto);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("https://opdb.org/machines/GRBE-MJL05", citation.SourceUrl);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
        Assert.Contains("GRBE-MJL05", citation.Title);
    }

    [Fact]
    public void Extract_GetMachineByTitleNullResult_ReturnsEmpty()
    {
        // The Wizard called the tool but the title didn't match anything.
        // No grounding ⇒ no citation.
        var response = BuildAgentResponseWithToolResult(
            functionName: "getMachineByTitle",
            result: null);

        var citations = Extractor.Extract(response);

        Assert.Empty(citations);
    }

    [Fact]
    public void Extract_GroundingDtoWithoutOpdbUrl_DoesNotProduceCitation()
    {
        // Defensive: a Machine record without an OpdbSourceUrl can exist
        // (Phase 1 reconciliation merged scraper-only entries before
        // OPDB sync). Without the URL there's no citable surface.
        var dto = new MachineGroundingDto(
            OpdbId: "ABC123",
            Title: "Foo",
            Manufacturer: "Stern",
            Year: 2021,
            Themes: [],
            Designers: [],
            OpdbSourceUrl: null,
            Editions: []);
        var response = BuildAgentResponseWithToolResult(
            functionName: "getMachineByTitle",
            result: dto);

        var citations = Extractor.Extract(response);

        Assert.Empty(citations);
    }

    [Fact]
    public void Extract_DuplicateGetMachineByTitleResults_DeduplicatesByUrl()
    {
        // The Wizard could call getMachineByTitle twice (e.g., to verify
        // a re-spelling). Duplicate URLs should collapse to one citation.
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", dto)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_2", dto)]));

        var citations = Extractor.Extract(response);

        Assert.Single(citations);
    }

    [Fact]
    public void Extract_SubAgentTextResult_MinesEmbeddedOpdbUrls()
    {
        // The Wizard called Valuation as a connected sub-agent (W1-1
        // wiring). The function result is the sub-agent's text reply,
        // which contains the sub-agent's grounded OPDB URL. The
        // tool-trace extractor mines the URL from the function-result
        // payload (NOT from the Wizard's outer prose), so a Wizard that
        // hallucinates an extra URL outside the function-result text
        // wouldn't be credited.
        const string subAgentText = """
            The Stern Godzilla (Premium) had an MSRP around $9,500 at launch.
            Source: https://opdb.org/machines/GRBE-MJL05
            """;
        var response = BuildAgentResponseWithToolResult(
            functionName: "Valuation",
            result: subAgentText);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("https://opdb.org/machines/GRBE-MJL05", citation.SourceUrl);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
    }

    [Fact]
    public void Extract_GetMachineByTitleAndSubAgentResults_UnionsCitations()
    {
        // Realistic shape: the Wizard calls getMachineByTitle to ground
        // a single-shot answer for one slice AND dispatches Valuation
        // for another slice. The two grounding paths produce distinct
        // OPDB URLs that union into the citation set.
        var godzilla = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        const string repairReply =
            "Service bulletin SB-21-04 covers the opto. https://opdb.org/machines/GRD8-MQR2N";

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", godzilla)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_2", repairReply)]));

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
        Assert.Contains(citations, c => c.MachineId == "GRBE-MJL05");
        Assert.Contains(citations, c => c.MachineId == "GRD8-MQR2N");
    }

    [Fact]
    public void Extract_SameUrlInDtoAndSubAgentText_Deduplicates()
    {
        // Cross-channel dedup: the Wizard called getMachineByTitle (which
        // returned the structured DTO) AND dispatched Valuation (whose
        // text reply also embedded the same OPDB URL). The single shared
        // seenUrls set in the extractor collapses these to one citation.
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        const string subAgentTextWithSameUrl =
            "Premium Godzilla cited at https://opdb.org/machines/GRBE-MJL05";

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", dto)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_2", subAgentTextWithSameUrl)]));

        var citations = Extractor.Extract(response);

        Assert.Single(citations);
    }

    [Fact]
    public void Extract_MultipleFunctionResultsInSingleMessage_AllProcessed()
    {
        // Microsoft.Agents.AI / Microsoft.Extensions.AI can pack multiple
        // FunctionResultContent into a single ChatMessage.Contents list
        // (one tool message containing N tool returns). The extractor's
        // inner `foreach (var content in message.Contents)` must visit
        // every entry.
        var godzilla = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var fooFighters = SampleGroundingDto(opdbId: "GRD8-MQR2N", title: "Foo Fighters (LE)");

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [
                new FunctionResultContent("call_1", godzilla),
                new FunctionResultContent("call_2", fooFighters)
            ]));

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
        Assert.Contains(citations, c => c.MachineId == "GRBE-MJL05");
        Assert.Contains(citations, c => c.MachineId == "GRD8-MQR2N");
    }

    [Fact]
    public void Extract_WizardOuterTextWithUrl_NotCounted()
    {
        // The Wizard's final text mentions an OPDB URL but no
        // function-result carried it. Per ADR-0022, this is exactly
        // the failure mode the new extractor exists to suppress —
        // hallucinated or paraphrased URLs in agent prose without
        // backing tool-call results don't count as citations.
        var response = BuildAgentResponse(
            new ChatMessage(
                ChatRole.Assistant,
                "I think the answer is at https://opdb.org/machines/FAKE-1."));

        var citations = Extractor.Extract(response);

        Assert.Empty(citations);
    }

    private static MachineGroundingDto SampleGroundingDto(string opdbId, string title)
    {
        return new MachineGroundingDto(
            OpdbId: opdbId,
            Title: title,
            Manufacturer: "Stern",
            Year: 2021,
            Themes: [],
            Designers: [],
            OpdbSourceUrl: $"https://opdb.org/machines/{opdbId}",
            Editions: []);
    }

    private static AgentResponse BuildAgentResponseWithToolResult(string functionName, object? result)
    {
        // FunctionResultContent's CallId is conventionally the tool-call's
        // synthetic identifier; we name it after the function so test
        // failures point at which tool-call produced an unexpected result.
        var content = new FunctionResultContent($"call_{functionName}", result);
        return BuildAgentResponse(new ChatMessage(ChatRole.Tool, [content]));
    }

    private static AgentResponse BuildAgentResponse(params ChatMessage[] messages)
    {
        return new AgentResponse(messages);
    }
}
