using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
// 2. searchCorpus results return a SearchCorpusResult whose Hits →
//    one Citation per unique DocumentUrl, page-anchored via SectionHeading
//    + page range in the title. Multiple chunks from the same DocumentId
//    collapse to one citation. The Wizard calls searchCorpus directly
//    (Wizard.md Step 4) before dispatching to sub-agents, so these
//    results appear in the Wizard's AgentResponse.Messages where this
//    extractor reads them. ADR-0022 § Negative consequence #4 notes a
//    Phase 5 layering for union-of-page-ranges across collapsed chunks.
//
// 3. Connected sub-agent function results (Valuation / Rules / Repair)
//    return the sub-agent's text response as a string. OPDB URLs in
//    that text are extracted via regex and become MachineRecord citations.
//    Sub-agents no longer call searchCorpus internally (removed in
//    fix/wizard-citation-extraction) — corpus retrieval moved to the
//    Wizard level where results are observable. The string regex arm
//    therefore fires only for opdb.org identity URLs that the sub-agent
//    echoes back in its answer prose.
//
// 4. The Wizard's final assistant text content is NOT scanned. Provenance
//    is preserved through the structural channel (tool-call results)
//    even if the Wizard's outer prose doesn't repeat every URL.
//
// NOTE on result types: AIFunctionFactory.Create (Microsoft.Extensions.AI)
// serializes C# return values to JSON before storing them in
// FunctionResultContent.Result. At runtime the result is a JsonElement,
// not the original typed object. The extractor dispatches on the JSON
// shape to determine the target type and deserializes before extracting.
// Unit tests use hand-constructed AgentResponse with typed objects (which
// the typed-check arms handle); the JSON arms cover the real Foundry path.
public sealed partial class ToolTraceCitationExtractor : ICitationExtractor
{
    private readonly ILogger<ToolTraceCitationExtractor> _logger;
    // Optional (fix/citation-metadata-channel): when wired, the sink
    // supplies Score + LastScrapedUtc that were stripped from the
    // model-facing FunctionResultContent.Result JSON because those fields
    // are [JsonIgnore] on SearchCorpusHit. When null (unit tests, legacy
    // callers without a scoped container), the typed C# arm still works
    // correctly — the hit's own properties carry the values on that path.
    private readonly IRetrievalCitationMetadataSink? _metadataSink;

    // DI constructor. Both parameters are optional so unit tests that
    // construct the extractor without a DI container continue to work
    // with zero changes. The sink default is null — on the typed-object
    // test path the C# properties carry Score/LastScrapedUtc directly;
    // the sink is only needed on the real Foundry JSON path where those
    // [JsonIgnore] fields are stripped by FunctionResultContent serialization.
    public ToolTraceCitationExtractor(
        ILogger<ToolTraceCitationExtractor>? logger = null,
        IRetrievalCitationMetadataSink? metadataSink = null)
    {
        _logger = logger ?? NullLogger<ToolTraceCitationExtractor>.Instance;
        _metadataSink = metadataSink;
    }

    // Matches both OPDB URL schemes: the legacy /machines/{id} form and
    // the /search?q={id} deep-link form that replaced it (PR #339 — the
    // /machines/ pages 404 because opdb.org uses internal numeric ids).
    // Stored data was migrated to /search?q= on 2026-06-10; the regex
    // accepts both so pre-migration text in old tool traces still
    // extracts.
    [GeneratedRegex(@"https://opdb\.org/(?:machines/|search\?q=)(?<id>[A-Z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpdbMachineUrlRegex();

    // AIFunctionFactory serializes function results with camelCase
    // property names ("opdbId", "hits") — verified live 2026-06-10
    // against gpt-4o via the Responses path. Property probing and
    // deserialization must be case-insensitive or the structured arms
    // silently never fire and every citation falls through to the URL
    // regex over raw JSON (the failure mode that took the deployed site
    // to a 100% refusal rate when the URL migration removed the
    // /machines/ URLs the regex depended on).
    private static readonly JsonSerializerOptions CaseInsensitiveJson =
        new(JsonSerializerDefaults.Web);

    // Deserialization can throw JsonException on an inner type mismatch
    // even after the outer shape probe passed (e.g. a numeric page field
    // arriving as a string). This extractor runs outside the router's
    // try/catch, so binding failures degrade to the regex fallback
    // instead of aborting the whole answer.
    //
    // Logs a Warning before falling through so operators can detect
    // JSON-shape drift (the 2026-06-10 citation outage class — invariant
    // #17 audit 2026-06-12). The functionCallId parameter identifies which
    // tool invocation triggered the failure so operators can correlate
    // with the trace via the call ID.
    private static T? TryDeserialize<T>(
        JsonElement element,
        string functionCallId,
        ILogger logger) where T : class
    {
        try
        {
            return element.Deserialize<T>(CaseInsensitiveJson);
        }
        catch (JsonException ex)
        {
            // Log at Warning before falling through to the regex fallback.
            // This is the 2026-06-10 outage class: a JSON-shape change in
            // the tool result silently bypassed citation extraction and
            // caused 100% refusals. Operators must see this on dashboards
            // before it reaches production impact.
            logger.LogWarning(ex,
                "ToolTraceCitationExtractor: JsonException deserializing {TargetType} from tool result " +
                "call '{FunctionCallId}' — falling through to OPDB URL regex. JSON-shape drift suspected.",
                typeof(T).Name,
                functionCallId);
            return null;
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string pascalCaseName,
        out JsonElement value)
    {
        if (element.TryGetProperty(pascalCaseName, out value))
        {
            return true;
        }

        // camelCase variant (first char lowered) — the runtime shape.
        var camel = string.Create(pascalCaseName.Length, pascalCaseName, static (span, name) =>
        {
            name.CopyTo(span);
            span[0] = char.ToLowerInvariant(span[0]);
        });
        return element.TryGetProperty(camel, out value);
    }

    public string SourceTag => "tool_trace";

    public IReadOnlyList<Citation> Extract(AgentResponse? response)
        => ExtractWithSourceIndex(response).Citations;

    // Returns both the deduplicated citation list and the ordered source index
    // where SourceIndex[k-1] is the SourceUrl of the k-th searchCorpus hit in
    // tool-trace order. This is the k→SourceUrl table the reconciler needs to
    // resolve [[cite:k]] markers in the model's answer.
    //
    // Only searchCorpus hits populate SourceIndex. getMachineByTitle results and
    // OPDB-regex citations from sub-agent text go into Citations only — they are
    // grounding records, not numbered sources the model cites with [[cite:k]].
    public (IReadOnlyList<Citation> Citations, IReadOnlyList<string> SourceIndex)
        ExtractWithSourceIndex(AgentResponse? response)
    {
        if (response is null)
        {
            return ([], []);
        }

        return ExtractFromMessagesWithIndex(response.Messages ?? []);
    }

    // Exposed as internal so AiRouter.AnswerStreamingAsync can extract
    // citations incrementally from a partially-accumulated message list
    // (per-FunctionResultContent CitationArrived emission in Wave 2
    // PR-S3). The public Extract(AgentResponse?) method delegates here.
    // Callers outside the streaming path should use the public method —
    // this helper is an implementation detail of the streaming pipeline,
    // not a general extension point.
    internal IReadOnlyList<Citation> ExtractFromMessages(IEnumerable<ChatMessage> messages)
        => ExtractFromMessagesWithIndex(messages).Citations;

    private (IReadOnlyList<Citation> Citations, IReadOnlyList<string> SourceIndex)
        ExtractFromMessagesWithIndex(IEnumerable<ChatMessage> messages)
    {
        // byUrl maps a citation's SourceUrl to its index in `citations` so a
        // later, RICHER citation can supersede an earlier bare one for the same
        // URL (see AddOrUpgrade) — not merely first-write-wins. This matters when
        // getMachineByTitle and searchCorpus both ground the same machine: their
        // citations share the OPDB URL, and the searchCorpus metadata-card chunk
        // (page anchor, freshness, relevance, synthesized content) must win over
        // the bare OPDB MachineRecord regardless of which tool fired first.
        var byUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>();
        var sourceIndex = new List<string>();

        // Null-coalesce both collections: Microsoft.Agents.AI 1.4.0
        // returns non-null in practice, but a malformed trace returning
        // null would bubble an NPE out of AiRouter (the call site sits
        // outside the wizard.RunAsync try/catch). Hardened symmetrically
        // with SubAgentTraceReader in W2-1.
        foreach (var message in messages)
        {
            foreach (var content in message.Contents ?? [])
            {
                if (content is not FunctionResultContent functionResult)
                {
                    continue;
                }

                ExtractFromFunctionResult(functionResult, byUrl, citations, sourceIndex);
            }
        }

        return (
            citations.Count == 0 ? [] : citations,
            sourceIndex.Count == 0 ? [] : sourceIndex);
    }

    private void ExtractFromFunctionResult(
        FunctionResultContent functionResult,
        Dictionary<string, int> byUrl,
        List<Citation> citations,
        List<string> sourceIndex)
    {
        var result = functionResult.Result;
        if (result is null)
        {
            return;
        }

        // FunctionResultContent carries CallId (not a name field). Use the
        // call ID as the identifier in the Warning log so operators can
        // correlate the failure with a specific tool invocation in traces.
        var functionCallId = functionResult.CallId ?? "<no-call-id>";

        // Typed object arm (unit tests and any future SDK that preserves type).
        if (result is MachineGroundingDto dto)
        {
            AddCitationFromGroundingDto(dto, byUrl, citations);
            // getMachineByTitle → Citations only, NOT sourceIndex (grounding record,
            // not a [[cite:k]]-numbered source).
            return;
        }

        if (result is SearchCorpusResult corpus)
        {
            AddCitationsFromCorpusHits(corpus.Hits, byUrl, citations, sourceIndex);
            return;
        }

        // JsonElement arm (real Foundry path via AIFunctionFactory.Create).
        // Dispatch on JSON shape: SearchCorpusResult has a top-level "Hits"
        // array; MachineGroundingDto has a top-level "OpdbId" string.
        if (result is JsonElement element)
        {
            ExtractFromJsonElement(element, functionCallId, byUrl, citations, sourceIndex);
            return;
        }

        // String result: sub-agent text response or an SDK path that
        // serializes to string rather than JsonElement. Extract OPDB URLs
        // via regex — the only structural anchor available in plain text.
        // These are OPDB identity citations, not corpus hits → Citations only.
        if (result is string text && !string.IsNullOrWhiteSpace(text))
        {
            AddCitationsFromText(text, byUrl, citations);
        }
    }

    private void ExtractFromJsonElement(
        JsonElement element,
        string functionCallId,
        Dictionary<string, int> byUrl,
        List<Citation> citations,
        List<string> sourceIndex)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // SearchCorpusResult shape: { "hits": [ ... ] } at runtime
            // (camelCase); "Hits" accepted for typed-test parity.
            if (TryGetPropertyIgnoreCase(element, "Hits", out var hitsElement)
                && hitsElement.ValueKind == JsonValueKind.Array)
            {
                if (TryDeserialize<SearchCorpusResult>(element, functionCallId, _logger) is { } corpusResult)
                {
                    AddCitationsFromCorpusHits(corpusResult.Hits, byUrl, citations, sourceIndex);
                    return;
                }
                // Shape probe matched but the payload didn't bind — fall
                // through to the URL regex below rather than dropping the
                // result silently (this extractor runs outside the
                // router's try/catch, so throwing would abort the answer).
            }
            else if (TryGetPropertyIgnoreCase(element, "OpdbId", out _))
            {
                // MachineGroundingDto shape: { "opdbId": "...", "opdbSourceUrl": "...", ... }
                // getMachineByTitle → Citations only, NOT sourceIndex.
                if (TryDeserialize<MachineGroundingDto>(element, functionCallId, _logger) is { } dto)
                {
                    AddCitationFromGroundingDto(dto, byUrl, citations);
                    return;
                }
            }
        }

        // Non-object or unrecognized shape — serialize to string and apply
        // OPDB URL regex. Covers JsonValueKind.String (sub-agent text reply
        // serialized as a JSON string), JsonValueKind.Null, and any
        // unrecognized JSON object shape. These are OPDB identity citations
        // → Citations only, NOT sourceIndex.
        var text = element.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            AddCitationsFromText(text, byUrl, citations);
        }
    }

    // Dedup + precedence: add a candidate citation unless its URL is already
    // present, EXCEPT that a richer CorpusChunk (page anchor + freshness +
    // relevance + synthesized content) supersedes an already-recorded bare
    // MachineRecord / CuratedLink for the same URL — replacing it in place to
    // preserve ordering. This is what lets a searchCorpus metadata-card chunk
    // win over the getMachineByTitle OPDB record they both point at, regardless
    // of which tool's result appeared first in the trace. Two CorpusChunks for
    // the same URL still collapse first-wins (first hit's anchor wins, per
    // ADR-0022); a MachineRecord never downgrades an existing CorpusChunk.
    private static void AddOrUpgrade(
        Dictionary<string, int> byUrl,
        List<Citation> citations,
        Citation candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.SourceUrl))
        {
            return;
        }

        if (byUrl.TryGetValue(candidate.SourceUrl, out var index))
        {
            if (candidate.SourceType == CitationSourceType.CorpusChunk
                && citations[index].SourceType != CitationSourceType.CorpusChunk)
            {
                citations[index] = candidate;
            }
            return;
        }

        byUrl[candidate.SourceUrl] = citations.Count;
        citations.Add(candidate);
    }

    private static void AddCitationFromGroundingDto(
        MachineGroundingDto dto,
        Dictionary<string, int> byUrl,
        List<Citation> citations)
    {
        if (string.IsNullOrWhiteSpace(dto.OpdbSourceUrl))
        {
            return;
        }

        AddOrUpgrade(byUrl, citations, new Citation(
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
    //
    // Two-channel design (fix/citation-metadata-channel, ADR-0035):
    // Score and LastScrapedUtc are [JsonIgnore] on SearchCorpusHit so
    // the model never sees retrieval internals. On the real Foundry path,
    // FunctionResultContent.Result is a JsonElement produced by
    // AIFunctionFactory.Create serializing the C# return value — and
    // [JsonIgnore] strips Score + LastScrapedUtc from that JSON, so
    // hit.Score and hit.LastScrapedUtc arrive as null here. The
    // _metadataSink (populated by SearchCorpusTool before the agent run)
    // carries those values out-of-band and is the fallback source.
    // Priority: typed C# hit values first (non-null when the typed arm
    // fires, e.g. unit tests); sink values second (non-null on the real
    // JSON path). Either or both may be null for pre-C3 chunks.
    // Per ADR-0022 § Algorithm step 2: each retrieved chunk is a
    // citation candidate; multiple chunks from the same DocumentId
    // collapse via the seenUrls set so the user sees one entry per
    // source document. The first hit's page range + section heading
    // wins for the title. Phase 5 may layer union-of-ranges across
    // collapsed chunks (ADR-0022 § Negative consequence #4); for
    // Phase 4, single-anchor citations are the contract.
    //
    // Two-channel design (fix/citation-metadata-channel, ADR-0035):
    // Score and LastScrapedUtc are [JsonIgnore] on SearchCorpusHit so
    // the model never sees retrieval internals. On the real Foundry path,
    // FunctionResultContent.Result is a JsonElement produced by
    // AIFunctionFactory.Create serializing the C# return value — and
    // [JsonIgnore] strips Score + LastScrapedUtc from that JSON, so
    // hit.Score and hit.LastScrapedUtc arrive as null here. The
    // _metadataSink (populated by SearchCorpusTool before the agent run)
    // carries those values out-of-band and is the fallback source.
    // Priority: typed C# hit values first (non-null when the typed arm
    // fires, e.g. unit tests); sink values second (non-null on the real
    // JSON path). Either or both may be null for pre-C3 chunks.
    //
    // sourceIndex: every hit's DocumentUrl is appended in tool-trace order,
    // regardless of dedup. This gives the reconciler the k→SourceUrl table
    // needed to resolve [[cite:k]] markers in the model's answer prose.
    // Hits with a blank DocumentUrl are skipped (they'd produce no citation
    // either) — the k-numbering must stay consistent with what the model saw.
    private void AddCitationsFromCorpusHits(
        IReadOnlyList<SearchCorpusHit> hits,
        Dictionary<string, int> byUrl,
        List<Citation> citations,
        List<string> sourceIndex)
    {
        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.DocumentUrl))
            {
                continue;
            }

            // Append to the k→SourceUrl table for every valid hit, in order.
            // The reconciler uses SourceIndex[k-1] to resolve [[cite:k]] markers.
            sourceIndex.Add(hit.DocumentUrl);

            // Look up UI metadata from the side channel. On the typed-object
            // path (unit tests) the sink is null or empty and hit.Score /
            // hit.LastScrapedUtc carry the values directly. On the real
            // Foundry JSON path both hit fields are null (stripped by
            // [JsonIgnore] during FunctionResultContent serialization) and
            // the sink is the authoritative source.
            RetrievalCitationMetadata? sinkMeta = null;
            _metadataSink?.TryGet(hit.DocumentUrl, out sinkMeta);

            var title = BuildCorpusCitationTitle(hit);
            AddOrUpgrade(byUrl, citations, new Citation(
                Title: title,
                SourceUrl: hit.DocumentUrl,
                MachineId: hit.MachineId,
                DocumentChunkId: hit.DocumentId,
                PageStart: hit.PageStart,
                PageEnd: hit.PageEnd,
                SectionHeading: string.IsNullOrWhiteSpace(hit.SectionHeading) ? null : hit.SectionHeading,
                SourceType: CitationSourceType.CorpusChunk,
                // Typed C# value wins (non-null on the unit-test / typed arm);
                // sink value is the fallback for the real Foundry JSON path
                // where [JsonIgnore] strips Score from FunctionResultContent.
                RelevanceScore: hit.Score ?? sinkMeta?.RelevanceScore,
                // Same two-channel pattern for LastScrapedUtc (ADR-0026 § 4).
                // Null for pre-C3 chunks regardless of path.
                LastScrapedUtc: hit.LastScrapedUtc ?? sinkMeta?.LastScrapedUtc));
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
        Dictionary<string, int> byUrl,
        List<Citation> citations)
    {
        var matches = OpdbMachineUrlRegex().Matches(text);
        foreach (Match match in matches)
        {
            var url = match.Value;

            // OPDB alias IDs have three dash-separated segments (e.g.
            // "Gj66Z-Mp4BN-A9Y6n"). Citations should point to base
            // machines, not edition aliases, so strip the third segment.
            // Base IDs always have two segments ("Gj66Z-Mp4BN").
            var rawId = match.Groups["id"].Value;
            var machineId = ToBaseMachineId(rawId);

            AddOrUpgrade(byUrl, citations, new Citation(
                Title: $"OPDB record {machineId}",
                SourceUrl: url,
                MachineId: machineId,
                DocumentChunkId: null,
                SourceType: CitationSourceType.MachineRecord));
        }
    }

    // Strips the alias (third) segment from OPDB IDs, keeping only the
    // base two-segment form. "Gj66Z-Mp4BN-A9Y6n" → "Gj66Z-Mp4BN".
    // IDs with two segments or fewer pass through unchanged.
    // Mirrors OpdbMachineMapper.GetBaseMachineOpdbId — same two-segment invariant.
    private static string ToBaseMachineId(string opdbId)
    {
        var first = opdbId.IndexOf('-');
        if (first < 0) return opdbId;
        var second = opdbId.IndexOf('-', first + 1);
        return second < 0 ? opdbId : opdbId[..second];
    }
}
