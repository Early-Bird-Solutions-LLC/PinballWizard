using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PinballWizard.Application.Ai.Citations;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Citations;

// The Phase 3 regex extractor is retained behind AiFoundryOptions
// .RetainRegexCitationCutover for the cutover observability window per
// ADR-0022. These tests pin its behavior so any drift between the
// tool-trace extractor and the regex one — surfaced via
// pinwiz.ai.citations.extracted_total{source=regex_legacy} — has a
// stable point of comparison. After H2 confirms parity, both this file
// and RegexLegacyCitationExtractor.cs are deleted.
public sealed class RegexLegacyCitationExtractorTests
{
    private static readonly RegexLegacyCitationExtractor Extractor = new();

    [Fact]
    public void SourceTag_IsRegexLegacy()
    {
        Assert.Equal("regex_legacy", Extractor.SourceTag);
    }

    [Fact]
    public void Extract_NullResponse_ReturnsEmpty()
    {
        Assert.Empty(Extractor.Extract(null));
    }

    [Fact]
    public void Extract_OpdbUrlInResponseText_ProducesCitation()
    {
        var response = new AgentResponse(new ChatMessage(
            ChatRole.Assistant,
            "Stern Godzilla — https://opdb.org/machines/GRBE-MJL05"));

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("https://opdb.org/machines/GRBE-MJL05", citation.SourceUrl);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
    }

    [Fact]
    public void Extract_NoOpdbUrls_ReturnsEmpty()
    {
        var response = new AgentResponse(new ChatMessage(
            ChatRole.Assistant,
            "Godzilla is a Stern pinball machine from 2021."));

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_DuplicateUrls_Deduplicates()
    {
        var response = new AgentResponse(new ChatMessage(
            ChatRole.Assistant,
            "Godzilla: https://opdb.org/machines/GRBE-MJL05 — also https://opdb.org/machines/GRBE-MJL05"));

        Assert.Single(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_MultipleDistinctUrls_AllProduceCitations()
    {
        var response = new AgentResponse(new ChatMessage(
            ChatRole.Assistant,
            "https://opdb.org/machines/GRBE-MJL05 and https://opdb.org/machines/GRD8-MQR2N"));

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
    }
}
