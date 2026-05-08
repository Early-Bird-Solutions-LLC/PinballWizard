using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PinballWizard.Application.Ai.Tools;

namespace PinballWizard.Application.Ai.Citations;

// Per ADR-0022, citations come from the agent's tool-call results, not
// from regex-matching the final response prose. This extractor walks
// AgentResponse.Messages and extracts citations from FunctionResultContent
// instances:
//
// 1. getMachineByTitle results return a MachineGroundingDto with
//    OpdbId + OpdbSourceUrl — those become a Citation directly. The DTO
//    is the authoritative grounding surface for OPDB-keyed answers.
//
// 2. Connected sub-agent function results (Valuation / Rules / Repair,
//    wired in W1-1) return the sub-agent's text response. Each sub-agent
//    is also instrumented with getMachineByTitle and instructed to
//    include the OPDB URL in its reply, so the embedded URL appears in
//    the function-result text. We extract OPDB URLs from that text via
//    the same regex pattern used in the legacy extractor — but applied
//    to function-result payloads rather than the Wizard's final prose,
//    so hallucinated URLs in the Wizard's outer text can no longer be
//    counted.
//
// 3. The Wizard's final assistant text content is NOT scanned. If the
//    Wizard summarizes/paraphrases the sub-agent's reply and drops the
//    URL from prose, that's a Wizard-prompt fidelity issue — but the
//    citation already exists on the function-result side. Provenance
//    is preserved through the structural channel even if the prose
//    representation drops it.
//
// Phase 4 will introduce searchCorpus as a sibling tool returning
// RetrievedChunk[] with document_url + page_range; this extractor will
// gain a corresponding case (build-spec § Phase 4 scope item 21
// adds searchCorpus; this extractor extends symmetrically). The
// public surface (ICitationExtractor) is stable across that addition.
public sealed partial class ToolTraceCitationExtractor : ICitationExtractor
{
    [GeneratedRegex(@"https://opdb\.org/machines/(?<id>[A-Z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpdbMachineUrlRegex();

    public string SourceTag => "tool_trace";

    public IReadOnlyList<Citation> Extract(AgentResponse? response)
    {
        if (response is null)
        {
            return Array.Empty<Citation>();
        }

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>();

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is not FunctionResultContent functionResult)
                {
                    continue;
                }

                ExtractFromFunctionResult(functionResult, seenUrls, citations);
            }
        }

        return citations.Count == 0 ? Array.Empty<Citation>() : citations;
    }

    private static void ExtractFromFunctionResult(
        FunctionResultContent functionResult,
        HashSet<string> seenUrls,
        List<Citation> citations)
    {
        // Microsoft.Extensions.AI's FunctionResultContent.Result is the
        // raw object the function returned (typed at the call site).
        // For getMachineByTitle that's a MachineGroundingDto?; for
        // sub-agent connected agents that's the sub-agent's text reply.
        var result = functionResult.Result;
        if (result is null)
        {
            return;
        }

        if (result is MachineGroundingDto dto)
        {
            AddCitationFromGroundingDto(dto, seenUrls, citations);
            return;
        }

        // String result: either a JSON-serialized DTO (some SDK call
        // paths serialize tool returns to string before placing them in
        // the trace) or a sub-agent's text response. In either case, the
        // OPDB URL is the structural anchor we want — extract via regex
        // applied to the function-result payload (NOT the agent's outer
        // prose, per ADR-0022).
        if (result is string text && !string.IsNullOrWhiteSpace(text))
        {
            AddCitationsFromText(text, seenUrls, citations);
        }
    }

    private static void AddCitationFromGroundingDto(
        MachineGroundingDto dto,
        HashSet<string> seenUrls,
        List<Citation> citations)
    {
        if (string.IsNullOrWhiteSpace(dto.OpdbSourceUrl))
        {
            return;
        }

        if (!seenUrls.Add(dto.OpdbSourceUrl))
        {
            return;
        }

        citations.Add(new Citation(
            Title: $"OPDB record {dto.OpdbId}",
            SourceUrl: dto.OpdbSourceUrl,
            MachineId: dto.OpdbId,
            DocumentChunkId: null));
    }

    private static void AddCitationsFromText(
        string text,
        HashSet<string> seenUrls,
        List<Citation> citations)
    {
        var matches = OpdbMachineUrlRegex().Matches(text);
        foreach (Match match in matches)
        {
            var url = match.Value;
            if (!seenUrls.Add(url))
            {
                continue;
            }

            citations.Add(new Citation(
                Title: $"OPDB record {match.Groups["id"].Value}",
                SourceUrl: url,
                MachineId: match.Groups["id"].Value,
                DocumentChunkId: null));
        }
    }
}
