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
// Phase 4 W4-1 (build-spec § scope item 21) extends this extractor
// with a searchCorpus arm: SearchCorpusResult.Hits → one Citation per
// unique DocumentId, page-anchored via SectionHeading + page range
// in the title. Multiple chunks from the same DocumentId collapse to
// one citation; ADR-0022 § Negative consequence #4 notes a Phase 5
// layering for union-of-page-ranges across collapsed chunks. The
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
            return [];
        }

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>();

        // Null-coalesce both collections: Microsoft.Agents.AI 1.4.0
        // returns non-null in practice, but a malformed trace returning
        // null would bubble an NPE out of AiRouter (the call site sits
        // outside the wizard.RunAsync try/catch). Hardened symmetrically
        // with SubAgentTraceReader in W2-1.
        foreach (var message in response.Messages ?? [])
        {
            foreach (var content in message.Contents ?? [])
            {
                if (content is not FunctionResultContent functionResult)
                {
                    continue;
                }

                ExtractFromFunctionResult(functionResult, seenUrls, citations);
            }
        }

        return citations.Count == 0 ? [] : citations;
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

        if (result is SearchCorpusResult corpus)
        {
            AddCitationsFromCorpusHits(corpus.Hits, seenUrls, citations);
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
            DocumentChunkId: null,
            SourceType: CitationSourceType.MachineRecord));
    }

    // Per ADR-0022 § Algorithm step 2: each retrieved chunk is a
    // citation candidate; multiple chunks from the same DocumentId
    // collapse via the seenUrls set so the user sees one entry per
    // source document. The first hit's page range + section heading
    // wins for the title. Phase 5 may layer union-of-ranges across
    // collapsed chunks (ADR-0022 § Negative consequence #4); for
    // Phase 4, single-anchor citations are the contract.
    private static void AddCitationsFromCorpusHits(
        IReadOnlyList<SearchCorpusHit> hits,
        HashSet<string> seenUrls,
        List<Citation> citations)
    {
        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.DocumentUrl))
            {
                continue;
            }

            if (!seenUrls.Add(hit.DocumentUrl))
            {
                continue;
            }

            var title = BuildCorpusCitationTitle(hit);
            citations.Add(new Citation(
                Title: title,
                SourceUrl: hit.DocumentUrl,
                MachineId: hit.MachineId,
                DocumentChunkId: hit.DocumentId,
                PageStart: hit.PageStart,
                PageEnd: hit.PageEnd,
                SectionHeading: string.IsNullOrWhiteSpace(hit.SectionHeading) ? null : hit.SectionHeading,
                SourceType: CitationSourceType.CorpusChunk));
        }
    }

    private static string BuildCorpusCitationTitle(SearchCorpusHit hit)
    {
        var section = string.IsNullOrWhiteSpace(hit.SectionHeading) ? null : hit.SectionHeading;
        var pageRange = hit.PageStart == hit.PageEnd
            ? $"p. {hit.PageStart}"
            : $"p. {hit.PageStart}–{hit.PageEnd}";
        var machine = string.IsNullOrWhiteSpace(hit.MachineTitle) ? null : hit.MachineTitle;

        if (machine is not null && section is not null)
        {
            return $"{machine} — {section} ({pageRange})";
        }
        if (machine is not null)
        {
            return $"{machine} ({pageRange})";
        }
        if (section is not null)
        {
            return $"{section} ({pageRange})";
        }
        return pageRange;
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
                DocumentChunkId: null,
                SourceType: CitationSourceType.MachineRecord));
        }
    }
}
