using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Citations;

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
    public void Extract_MachineCitation_ThreadsFreshnessFromSink()
    {
        // Machine freshness (OPDB LastSeenAt) travels out-of-band via the metadata
        // sink keyed by OpdbSourceUrl — the model never sees the timestamp. The machine
        // citation's LastScrapedUtc must be enriched from the sink so the FreshnessBadge
        // shows "synced N ago" instead of "freshness unknown".
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var synced = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var sink = new RetrievalCitationMetadataSink();
        sink.Record(dto.OpdbSourceUrl!, new RetrievalCitationMetadata(LastScrapedUtc: synced, RelevanceScore: null));
        var extractor = new ToolTraceCitationExtractor(metadataSink: sink);
        var response = BuildAgentResponseWithToolResult(functionName: "getMachineByTitle", result: dto);

        var citation = Assert.Single(extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Equal(synced, citation.LastScrapedUtc);
    }

    [Fact]
    public void Extract_MachineCitation_NoSinkEntry_LeavesFreshnessNull()
    {
        // No sink entry (machine not seen this run, or the typed-object path) → freshness
        // stays null and the FreshnessBadge renders "freshness unknown" gracefully.
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var extractor = new ToolTraceCitationExtractor(metadataSink: new RetrievalCitationMetadataSink());
        var response = BuildAgentResponseWithToolResult(functionName: "getMachineByTitle", result: dto);

        var citation = Assert.Single(extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Null(citation.LastScrapedUtc);
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
            Editions: [],
            GroupId: null,
            Siblings: [],
            TitleCollisions: []);
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
    public void Extract_SubAgentTextContainsAliasId_NormalizesToBaseMachineId()
    {
        // OPDB alias IDs have a third dash-separated segment (e.g.
        // "Gj66Z-Mp4BN-A9Y6n"). Sub-agent prose may embed the full alias
        // URL. The extractor must strip the alias suffix so citations point
        // to the base machine, not an edition alias that won't be in the
        // expected set during eval.
        const string subAgentText = """
            Halloween rules sourced from https://opdb.org/machines/Gj66Z-Mp4BN-A9Y6n
            """;
        var response = BuildAgentResponseWithToolResult(
            functionName: "Rules",
            result: subAgentText);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("Gj66Z-Mp4BN", citation.MachineId);
    }

    [Fact]
    public void Extract_SubAgentTextContainsBaseId_PassesThroughUnchanged()
    {
        // Base IDs (two dash-separated segments) must not be mutated.
        const string subAgentText = "Source: https://opdb.org/machines/Gj66Z-Mp4BN";
        var response = BuildAgentResponseWithToolResult(
            functionName: "Rules",
            result: subAgentText);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("Gj66Z-Mp4BN", citation.MachineId);
    }

    [Fact]
    public void Extract_SubAgentTextContainsFourSegmentId_StripsToTwoSegments()
    {
        // Four-segment IDs (e.g. from an unexpected OPDB alias extension)
        // must truncate at the second dash, keeping the two-segment base.
        // Ensures ToBaseMachineId is not sensitive to additional segments
        // beyond the three-segment alias form.
        const string subAgentText = "Source: https://opdb.org/machines/Gj66Z-Mp4BN-A9Y6n-Extra";
        var response = BuildAgentResponseWithToolResult(
            functionName: "Rules",
            result: subAgentText);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("Gj66Z-Mp4BN", citation.MachineId);
    }

    [Fact]
    public void Extract_SubAgentTextContainsSingleSegmentId_PassesThroughUnchanged()
    {
        // Single-segment IDs (no dash) must pass through unchanged rather
        // than returning empty string. Guards the first IndexOf('-') < 0 branch.
        const string subAgentText = "Source: https://opdb.org/machines/GRBE5";
        var response = BuildAgentResponseWithToolResult(
            functionName: "Rules",
            result: subAgentText);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("GRBE5", citation.MachineId);
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

    [Fact]
    public void Extract_SearchCorpusResult_ProducesOneCitationPerDocumentId()
    {
        // Per ADR-0022 § Algorithm step 2: multiple chunks from the
        // same document collapse to one citation. Two chunks from
        // doc_x and one from doc_y → exactly two citations.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_x", documentUrl: "https://example/manual_x.pdf",
                      machineId: "GRBE-MJL05", section: "Section A", pageStart: 1, pageEnd: 1),
            SampleHit(documentId: "doc_x", documentUrl: "https://example/manual_x.pdf",
                      machineId: "GRBE-MJL05", section: "Section B", pageStart: 5, pageEnd: 6),
            SampleHit(documentId: "doc_y", documentUrl: "https://example/bulletin_y.pdf",
                      machineId: "GRBE-MJL05", section: "Bulletin Top", pageStart: 1, pageEnd: 1),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
        Assert.Contains(citations, c => c.SourceUrl == "https://example/manual_x.pdf");
        Assert.Contains(citations, c => c.SourceUrl == "https://example/bulletin_y.pdf");
    }

    [Fact]
    public void Extract_SearchCorpusResult_TitleCarriesPageRangeAndSection()
    {
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/doc_a.pdf",
                      machineTitle: "Godzilla (Premium)", section: "Coil Replacement",
                      pageStart: 12, pageEnd: 14),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Contains("Godzilla (Premium)", citation.Title);
        Assert.Contains("Coil Replacement", citation.Title);
        Assert.Contains("12", citation.Title);
        Assert.Contains("14", citation.Title);
    }

    [Fact]
    public void Extract_SearchCorpusResult_SinglePage_TitleUsesSinglePageForm()
    {
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/doc_a.pdf",
                      pageStart: 7, pageEnd: 7),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Contains("p. 7", citation.Title);
        Assert.DoesNotContain("p. 7–7", citation.Title);
    }

    [Fact]
    public void Extract_SearchCorpusResult_PopulatesDocumentChunkIdAndMachineId()
    {
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/doc_a.pdf",
                      machineId: "GRBE-MJL05"),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("doc_a", citation.DocumentChunkId);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
    }

    [Fact]
    public void Extract_SearchCorpusResult_EmptyHits_NoCitations()
    {
        var response = BuildAgentResponseWithToolResult("searchCorpus", new SearchCorpusResult([]));

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_SearchCorpusResult_BlankUrl_Skipped()
    {
        // Defensive: an indexer bug could produce empty document_url;
        // the extractor must not produce a citation with empty SourceUrl.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: ""),
            SampleHit(documentId: "doc_b", documentUrl: "https://example/ok.pdf"),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("https://example/ok.pdf", citation.SourceUrl);
    }

    [Fact]
    public void Extract_GetMachineByTitleAndSearchCorpus_BothChannels_UnionsCitations()
    {
        // End-to-end shape: the Wizard's trace contains both an OPDB
        // grounding result AND a corpus retrieval. Both surface as
        // citations; the seenUrls dedup is keyed by SourceUrl, so a
        // distinct OPDB URL and a distinct manual URL count separately.
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      machineId: "GRBE-MJL05"),
        ]);

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", dto)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_2", corpus)]));

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
        Assert.Contains(citations, c => c.SourceUrl == "https://opdb.org/machines/GRBE-MJL05");
        Assert.Contains(citations, c => c.SourceUrl == "https://example/manual.pdf");
    }

    // -------------------------------------------------------------------------
    // PR-C2 Wave 2: RelevanceScore threaded from SearchCorpusHit.Score
    // -------------------------------------------------------------------------

    [Fact]
    public void Extract_populates_RelevanceScore_from_SearchCorpusHit_Score()
    {
        // The citation extractor reads Score (which is [JsonIgnore] on
        // SearchCorpusHit so the model never sees it) and threads it onto
        // Citation.RelevanceScore for the frontend CitationCard to render.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      score: 0.85),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(0.85, citation.RelevanceScore);
    }

    [Fact]
    public void Extract_handles_null_Score_gracefully()
    {
        // Score is null when the retriever bypassed the semantic re-ranker
        // (e.g. pure keyword query path). The citation must not throw and
        // must leave RelevanceScore null so the frontend tolerates absence.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      score: null),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Null(citation.RelevanceScore);
    }

    // -------------------------------------------------------------------------
    // PR-C3 Wave 2: LastScrapedUtc threaded from SearchCorpusHit → Citation
    // -------------------------------------------------------------------------

    [Fact]
    public void Extract_populates_LastScrapedUtc_from_SearchCorpusHit()
    {
        // The citation extractor reads LastScrapedUtc (which is [JsonIgnore]
        // on SearchCorpusHit so the model never sees it) and threads it onto
        // Citation.LastScrapedUtc for the frontend CitationCard freshness badge
        // (ADR-0026 § 4). Uses a real non-default timestamp to confirm the
        // value flows through — default(DateTimeOffset) would not distinguish
        // "threaded null" from "threaded zero" bugs.
        var expectedTs = new DateTimeOffset(2026, 3, 22, 14, 30, 0, TimeSpan.Zero);
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      lastScrapedUtc: expectedTs),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(expectedTs, citation.LastScrapedUtc);
    }

    [Fact]
    public void Extract_handles_null_LastScrapedUtc_gracefully()
    {
        // Chunks indexed before PR-C3 (or from scrapers that didn't populate
        // Timeline.LastDownloadedAt) carry null. The extractor must not throw
        // and must leave Citation.LastScrapedUtc null so the frontend
        // freshness badge is conditionally rendered rather than showing a
        // misleading epoch timestamp.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      lastScrapedUtc: null),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Null(citation.LastScrapedUtc);
    }

    // -------------------------------------------------------------------------
    // PR-C1 Wave 1: Citation DTO widening — SourceType + page anchors
    // -------------------------------------------------------------------------

    [Fact]
    public void Extract_CorpusHit_SourceTypeIsCorpusChunk()
    {
        // ADR-0026 § 8: searchCorpus hits → CorpusChunk source type.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf"),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.CorpusChunk, citation.SourceType);
    }

    [Fact]
    public void Extract_CorpusHit_PageAnchorsAndSectionHeadingPopulated()
    {
        // Wave 1 populates page anchors + section heading from
        // SearchCorpusHit immediately; LastScrapedUtc / RelevanceScore
        // stay null until PR-C2/C3.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      machineId: "GRBE-MJL05", section: "Coil Replacement",
                      pageStart: 12, pageEnd: 15),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(12, citation.PageStart);
        Assert.Equal(15, citation.PageEnd);
        Assert.Equal("Coil Replacement", citation.SectionHeading);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
        Assert.Equal("doc_a", citation.DocumentChunkId);
        Assert.Null(citation.LastScrapedUtc);
        Assert.Null(citation.RelevanceScore);
    }

    [Fact]
    public void Extract_CorpusHit_SinglePage_PageStartEqualsPageEnd()
    {
        // When PageStart == PageEnd the Citation fields are both
        // populated identically — the title formatter handles the
        // "p. N" rendering, but both raw fields carry the value.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/doc_a.pdf",
                      pageStart: 7, pageEnd: 7),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(7, citation.PageStart);
        Assert.Equal(7, citation.PageEnd);
    }

    [Fact]
    public void Extract_CorpusHit_WhitespaceSectionHeading_CitationSectionHeadingIsNull()
    {
        // Empty or whitespace-only SectionHeading on the hit must not
        // propagate as an empty string — the Citation field must be null
        // so the frontend can test `!= null` rather than also trimming.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/doc_a.pdf",
                      section: "   "),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Null(citation.SectionHeading);
    }

    [Fact]
    public void Extract_GroundingDto_SourceTypeIsMachineRecord()
    {
        // getMachineByTitle result → MachineRecord source type.
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var response = BuildAgentResponseWithToolResult("getMachineByTitle", dto);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Null(citation.PageStart);
        Assert.Null(citation.PageEnd);
        Assert.Null(citation.SectionHeading);
    }

    [Fact]
    public void Extract_RegexFallbackOpdbUrl_SourceTypeIsMachineRecord()
    {
        // Regex-extracted OPDB URLs from sub-agent text → MachineRecord.
        const string subAgentText = "See https://opdb.org/machines/GRBE-MJL05 for details.";
        var response = BuildAgentResponseWithToolResult("Valuation", subAgentText);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Null(citation.PageStart);
        Assert.Null(citation.PageEnd);
    }

    [Fact]
    public void Extract_MultipleCorpusHitsSameDocumentUrl_CollapsesToOneCitation_ExistingBehaviorPreserved()
    {
        // Dedup (keyed by DocumentUrl) continues to work correctly
        // after the DTO widening — first-hit wins for page anchors.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_x", documentUrl: "https://example/manual_x.pdf",
                      section: "Section A", pageStart: 1, pageEnd: 1),
            SampleHit(documentId: "doc_x", documentUrl: "https://example/manual_x.pdf",
                      section: "Section B", pageStart: 5, pageEnd: 6),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal("https://example/manual_x.pdf", citation.SourceUrl);
        // First hit wins: page anchors and section from the first entry.
        Assert.Equal(1, citation.PageStart);
        Assert.Equal(1, citation.PageEnd);
        Assert.Equal("Section A", citation.SectionHeading);
    }

    private static SearchCorpusHit SampleHit(
        string documentId,
        string documentUrl,
        string machineId = "GRBE-MJL05",
        string machineTitle = "Godzilla (Premium)",
        string section = "Section",
        int pageStart = 1,
        int pageEnd = 1,
        double? score = null,
        DateTimeOffset? lastScrapedUtc = null)
    {
        return new SearchCorpusHit(
            MachineId: machineId,
            MachineTitle: machineTitle,
            DocumentId: documentId,
            DocumentUrl: documentUrl,
            DocumentType: "manual",
            PageStart: pageStart,
            PageEnd: pageEnd,
            SectionHeading: section,
            Content: "chunk content")
        {
            Score = score,
            LastScrapedUtc = lastScrapedUtc,
        };
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
            Editions: [],
            GroupId: null,
            Siblings: [],
            TitleCollisions: []);
    }

    // ── JsonElement arm (live Foundry path) ──────────────────────────────
    // AIFunctionFactory.Create serializes C# return values to JSON before
    // storing them in FunctionResultContent.Result, so real Foundry calls
    // produce JsonElement rather than typed objects. These tests cover that
    // path using hand-serialized JsonElements that match the live shape.

    [Fact]
    public void Extract_SearchCorpusResult_AsJsonElement_ProducesCitations()
    {
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual_a.pdf",
                      machineId: "GRBE-MJL05", section: "Coil Replacement",
                      pageStart: 12, pageEnd: 14),
        ]);
        var element = JsonSerializer.SerializeToElement(corpus);
        var response = BuildAgentResponseWithToolResult("searchCorpus", element);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("https://example/manual_a.pdf", citation.SourceUrl);
        Assert.Equal(CitationSourceType.CorpusChunk, citation.SourceType);
    }

    [Fact]
    public void Extract_SearchCorpusResult_AsJsonElement_EmptyHits_NoCitations()
    {
        var element = JsonSerializer.SerializeToElement(new SearchCorpusResult([]));
        var response = BuildAgentResponseWithToolResult("searchCorpus", element);

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_MachineGroundingDto_AsJsonElement_ProducesCitation()
    {
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var element = JsonSerializer.SerializeToElement(dto);
        var response = BuildAgentResponseWithToolResult("getMachineByTitle", element);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal($"https://opdb.org/machines/GRBE-MJL05", citation.SourceUrl);
        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
    }

    [Fact]
    public void Extract_MachineGroundingDto_AsJsonElement_NullOpdbSourceUrl_NoCitation()
    {
        var dto = new MachineGroundingDto(
            OpdbId: "GRBE-MJL05", Title: "Godzilla (Premium)",
            Manufacturer: "Stern", Year: 2021,
            Themes: [], Designers: [], OpdbSourceUrl: null,
            Editions: [], GroupId: null, Siblings: [], TitleCollisions: []);
        var element = JsonSerializer.SerializeToElement(dto);
        var response = BuildAgentResponseWithToolResult("getMachineByTitle", element);

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_JsonElement_StringKind_ExtractsOpdbUrlsViaRegex()
    {
        // Sub-agent text responses serialized as JSON string values (not objects)
        // should fall through to the OPDB URL regex arm without throwing.
        const string subAgentText = "Source: https://opdb.org/machines/GRBE-MJL05";
        var element = JsonSerializer.SerializeToElement(subAgentText);
        var response = BuildAgentResponseWithToolResult("Rules", element);

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("https://opdb.org/machines/GRBE-MJL05", citation.SourceUrl);
    }

    [Fact]
    public void Extract_JsonElement_StringKind_NoOpdbUrl_NoCitation()
    {
        // A JSON string element with no recognizable URL should produce no citations
        // and must not throw (handles "Error: Function failed." from Foundry).
        var element = JsonSerializer.SerializeToElement("Error: Function failed.");
        var response = BuildAgentResponseWithToolResult("Rules", element);

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_JsonElement_UnrecognizedObjectShape_FallsThroughToRegex()
    {
        // An object with neither "Hits" nor "OpdbId" falls through to the
        // OPDB URL regex on its JSON string representation.
        var element = JsonSerializer.SerializeToElement(new { SomeField = "https://opdb.org/machines/GRBE-MJL05" });
        var response = BuildAgentResponseWithToolResult("UnknownTool", element);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("https://opdb.org/machines/GRBE-MJL05", citation.SourceUrl);
    }

    [Fact]
    public void Extract_JsonElement_NullKind_NoCitation()
    {
        // JsonValueKind.Null → element.ToString() returns "null" → whitespace
        // guard discards it. Must not throw.
        var element = JsonSerializer.SerializeToElement<string?>(null);
        var response = BuildAgentResponseWithToolResult("UnknownTool", element);

        Assert.Empty(Extractor.Extract(response));
    }

    // ── camelCase JsonElement arm (2026-06-10 outage regression) ─────────
    // AIFunctionFactory serializes function results with CAMELCASE property
    // names ("opdbId", "hits") — verified live against gpt-4o. The tests
    // above serialize with default (PascalCase) options, which is why they
    // stayed green while the deployed site extracted zero citations and
    // refused 100% of questions: the property probes were case-sensitive,
    // every result fell through to the URL regex, and the /search?q= data
    // migration removed the last URLs that regex could match. These tests
    // pin the LIVE shape.

    private static readonly JsonSerializerOptions LiveCamelCaseJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Extract_MachineGroundingDto_AsCamelCaseJsonElement_ProducesCitation()
    {
        // The live-shape repro of the 2026-06-10 outage: camelCase DTO with
        // a post-migration /search?q= source URL must still produce the
        // structured MachineRecord citation.
        var dto = SampleGroundingDto(opdbId: "GweeP-MW95j", title: "Godzilla (Pro)") with
        {
            OpdbSourceUrl = "https://opdb.org/search?q=GweeP-MW95j",
        };
        var element = JsonSerializer.SerializeToElement(dto, LiveCamelCaseJson);
        var response = BuildAgentResponseWithToolResult("GetMachineByTitle", element);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("https://opdb.org/search?q=GweeP-MW95j", citation.SourceUrl);
        Assert.Equal("GweeP-MW95j", citation.MachineId);
        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
    }

    [Fact]
    public void Extract_SearchCorpusResult_AsCamelCaseJsonElement_ProducesCitations()
    {
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual_a.pdf",
                      machineId: "GweeP-MW95j", section: "Multiball Rules",
                      pageStart: 31, pageEnd: 33),
        ]);
        var element = JsonSerializer.SerializeToElement(corpus, LiveCamelCaseJson);
        var response = BuildAgentResponseWithToolResult("SearchCorpus", element);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("https://example/manual_a.pdf", citation.SourceUrl);
        Assert.Equal("GweeP-MW95j", citation.MachineId);
        Assert.Equal(CitationSourceType.CorpusChunk, citation.SourceType);
        Assert.Equal(31, citation.PageStart);
        Assert.Equal("Multiball Rules", citation.SectionHeading);
    }

    // ── /search?q= URL regex arm (2026-06-10 migration) ──────────────────

    [Fact]
    public void Extract_SubAgentTextWithSearchQueryUrl_MinesCitation()
    {
        // Post-migration OPDB deep links use /search?q={id}; sub-agent prose
        // echoing them must still mine a MachineRecord citation.
        const string subAgentText = "Per the record at https://opdb.org/search?q=GweeP-MW95j, it released in 2021.";
        var response = BuildAgentResponseWithToolResult("Rules", subAgentText);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("https://opdb.org/search?q=GweeP-MW95j", citation.SourceUrl);
        Assert.Equal("GweeP-MW95j", citation.MachineId);
    }

    [Fact]
    public void Extract_SearchQueryUrlWithAliasId_NormalizesToBaseMachineId()
    {
        // Alias-id stripping (third dash segment) applies to the /search?q=
        // form exactly as it does to the legacy /machines/ form.
        const string subAgentText = "See https://opdb.org/search?q=Gj66Z-Mp4BN-A9Y6n for the edition record.";
        var response = BuildAgentResponseWithToolResult("Valuation", subAgentText);

        var citation = Assert.Single(Extractor.Extract(response));
        Assert.Equal("Gj66Z-Mp4BN", citation.MachineId);
    }

    [Fact]
    public void Extract_CorpusShapeWithMalformedHit_DoesNotThrow_FallsThroughToRegex()
    {
        // The shape probe ("hits" array) can pass while inner binding
        // fails (numeric page field arriving as a string). The extractor
        // runs outside the router's try/catch, so it must degrade to the
        // URL regex over the raw JSON rather than throw and abort the
        // whole answer.
        using var malformed = JsonDocument.Parse(
            """{"hits":[{"machineId":"GweeP-MW95j","documentUrl":"https://opdb.org/search?q=GweeP-MW95j","pageStart":"twelve"}]}""");
        var response = BuildAgentResponseWithToolResult("SearchCorpus", malformed.RootElement.Clone());

        var citations = Extractor.Extract(response);

        var citation = Assert.Single(citations);
        Assert.Equal("GweeP-MW95j", citation.MachineId);
    }

    [Fact]
    public void Extract_MixedLegacyAndSearchQueryUrls_BothMined()
    {
        // Old tool traces / cached answers may still carry /machines/ URLs;
        // both schemes extract side by side.
        const string subAgentText =
            "Compare https://opdb.org/machines/GRBE-MJL05 with https://opdb.org/search?q=GweeP-MW95j.";
        var response = BuildAgentResponseWithToolResult("Rules", subAgentText);

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
        Assert.Contains(citations, c => c.MachineId == "GRBE-MJL05");
        Assert.Contains(citations, c => c.MachineId == "GweeP-MW95j");
    }

    // ── Invariant #17 audit 2026-06-12: item 4 ───────────────────────────────
    // TryDeserialize: malformed JSON that passes the shape probe but fails
    // binding must (a) log a Warning and (b) fall through to the URL regex
    // without throwing.

    [Fact]
    public void Extract_MalformedJsonShapeHit_FallsThroughToRegexWithoutThrowing()
    {
        // Build a JSON object where:
        //   - "hits" is an array (passes the shape probe: hitsElement.ValueKind == Array)
        //   - but the element inside has "pageStart" as a quoted string instead of an int,
        //     which causes Deserialize<SearchCorpusResult> to throw a JsonException
        //     (System.Text.Json strict number handling rejects "bad-value" for int).
        //
        // Behavioral assertion: Extract must not throw; instead it falls
        // through to the OPDB URL regex — which will find the embedded URL
        // in the toString() of the JsonElement.
        var malformed = JsonSerializer.SerializeToElement(new
        {
            hits = new[]
            {
                new
                {
                    machineId = "GRBE-MJL05",
                    machineTitle = "Test",
                    documentId = "doc1",
                    documentUrl = "https://opdb.org/search?q=GRBE-MJL05",
                    documentType = "manual",
                    pageStart = "not-a-number",  // string instead of int — triggers JsonException
                    pageEnd = "not-a-number",
                    sectionHeading = "intro",
                    content = "Some content.",
                }
            },
        });

        var capturingLogger = new CapturingExtractorLogger();
        var extractor = new ToolTraceCitationExtractor(capturingLogger);
        var response = BuildAgentResponseWithToolResult("searchCorpus", malformed);

        // Must not throw — falls through to the URL regex.
        extractor.Extract(response);

        // A Warning must have been logged for the deserialization failure
        // (the 2026-06-10 outage class detection — invariant #17 audit).
        Assert.True(capturingLogger.WarningCount > 0,
            "Expected at least one Warning log when JsonException occurs during TryDeserialize.");
    }

    [Fact]
    public void Extract_MalformedMachineGroundingDtoJson_FallsThroughToRegexWithoutThrowing()
    {
        // Same pattern for MachineGroundingDto: shape probe finds "OpdbId" but
        // field types are wrong so Deserialize<MachineGroundingDto> throws.
        var malformed = JsonSerializer.SerializeToElement(new
        {
            opdbId = 12345,            // numeric instead of string — triggers JsonException
            opdbSourceUrl = "https://opdb.org/search?q=GRBE-MJL05",
        });

        var capturingLogger = new CapturingExtractorLogger();
        var extractor = new ToolTraceCitationExtractor(capturingLogger);
        var response = BuildAgentResponseWithToolResult("getMachineByTitle", malformed);

        // Must not throw.
        extractor.Extract(response);

        Assert.True(capturingLogger.WarningCount > 0,
            "Expected at least one Warning log when JsonException occurs during TryDeserialize on a MachineGroundingDto.");
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

    // ── fix/citation-metadata-channel: JSON-path regression tests (ADR-0035) ───
    //
    // These tests reproduce the PRODUCTION bug: on the real Foundry path,
    // FunctionResultContent.Result is a JsonElement (not a typed C# object),
    // and [JsonIgnore] on SearchCorpusHit.Score / .LastScrapedUtc strips those
    // fields from that JSON. The existing tests above use typed objects and
    // therefore never exposed the bug — the JSON path always produced null
    // freshness + relevance in production.
    //
    // The fix: SearchCorpusTool records the metadata into a request-scoped
    // IRetrievalCitationMetadataSink keyed by DocumentUrl; the extractor
    // enriches citations from the sink when the typed hit fields are null.
    //
    // Two critical tests:
    //   1. POSITIVE — JSON path + sink wired → citation has correct values.
    //   2. NEGATIVE — JSON path WITHOUT sink → values are null (documents
    //      WHY the sink is necessary; the [JsonIgnore] strips the data).

    private static readonly JsonSerializerOptions WebCamelCaseJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Extract_JsonPath_WithSink_PopulatesLastScrapedUtcAndRelevanceScore()
    {
        // Arrange: serialize a SearchCorpusResult to a JsonElement the same
        // way AIFunctionFactory.Create would on the real Foundry path. The
        // [JsonIgnore] fields (Score, LastScrapedUtc) are stripped from the
        // element — they arrive as null when the hit is deserialized back.
        // The sink carries them out-of-band, bridging the gap.
        var expectedTs = new DateTimeOffset(2026, 3, 22, 14, 30, 0, TimeSpan.Zero);
        const double expectedScore = 0.87;
        const string docUrl = "https://sternpinball.com/godzilla_manual.pdf";

        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: docUrl,
                      machineId: "GRBE-MJL05", section: "Coil Replacement",
                      pageStart: 12, pageEnd: 14,
                      score: expectedScore, lastScrapedUtc: expectedTs),
        ]);
        // Serialize with camelCase (live Foundry shape). [JsonIgnore] strips
        // Score + LastScrapedUtc — this is the exact shape the extractor sees
        // in production.
        var jsonElement = JsonSerializer.SerializeToElement(corpus, WebCamelCaseJson);

        // Populate the sink with the metadata that was stripped from the JSON.
        var sink = new RetrievalCitationMetadataSink();
        sink.Record(docUrl, new RetrievalCitationMetadata(expectedTs, expectedScore));

        // Extractor constructed WITH the sink (the production wiring).
        var extractor = new ToolTraceCitationExtractor(metadataSink: sink);
        var response = BuildAgentResponseWithToolResult("searchCorpus", jsonElement);

        var citation = Assert.Single(extractor.Extract(response));

        // The sink bridge must restore both values even though they were
        // stripped from the JSON that the extractor's deserialization reads.
        Assert.Equal(expectedTs, citation.LastScrapedUtc);
        Assert.Equal(expectedScore, citation.RelevanceScore);
    }

    [Fact]
    public void Extract_JsonPath_WithoutSink_LastScrapedUtcAndRelevanceScoreAreNull()
    {
        // WHY-test for the sink: this test documents exactly WHY the
        // IRetrievalCitationMetadataSink was introduced. On the real Foundry
        // JSON path, [JsonIgnore] strips Score + LastScrapedUtc from the
        // FunctionResultContent.Result before the extractor reads it. Without
        // the sink, both fields are null on every citation in production —
        // resulting in "freshness unknown" on every citation card.
        //
        // If this test starts failing (values are no longer null), it means
        // [JsonIgnore] was removed and the model can now see the fields —
        // which is a regression (the model must NOT see retrieval internals).
        var expectedTs = new DateTimeOffset(2026, 3, 22, 14, 30, 0, TimeSpan.Zero);
        const double expectedScore = 0.87;
        const string docUrl = "https://sternpinball.com/godzilla_manual.pdf";

        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: docUrl,
                      score: expectedScore, lastScrapedUtc: expectedTs),
        ]);
        // Same JSON-path serialization as the positive test above.
        var jsonElement = JsonSerializer.SerializeToElement(corpus, WebCamelCaseJson);

        // Extractor constructed WITHOUT a sink (the broken pre-fix state).
        var extractorWithoutSink = new ToolTraceCitationExtractor();
        var response = BuildAgentResponseWithToolResult("searchCorpus", jsonElement);

        var citation = Assert.Single(extractorWithoutSink.Extract(response));

        // [JsonIgnore] strips both fields from the JSON → both null when no
        // sink is present to compensate. This is the production bug this PR fixes.
        Assert.Null(citation.LastScrapedUtc);
        Assert.Null(citation.RelevanceScore);
    }

    // Citation dedup precedence: getMachineByTitle and searchCorpus both ground
    // the same machine, so their citations share the OPDB URL. The RICHER
    // searchCorpus CorpusChunk (page anchor + freshness + relevance + content)
    // must win over the bare getMachineByTitle MachineRecord — regardless of
    // which tool's result appears first in the trace. Before this fix, the
    // first-seen URL won, so a valuation answer that called getMachineByTitle
    // first surfaced only the bare OPDB record (no freshness, no relevance).
    // Exercises the real Foundry JSON path.
    [Theory]
    [InlineData(true)]   // getMachineByTitle result first, then searchCorpus
    [InlineData(false)]  // searchCorpus result first, then getMachineByTitle
    public void Extract_CorpusChunk_supersedes_MachineRecord_for_same_url(bool machineRecordFirst)
    {
        const string opdbId = "GRBE-MJL05";
        var sharedUrl = $"https://opdb.org/machines/{opdbId}"; // == SampleGroundingDto.OpdbSourceUrl
        var expectedTs = new DateTimeOffset(2026, 6, 9, 21, 0, 0, TimeSpan.Zero);
        const double expectedScore = 1.94;

        var dto = SampleGroundingDto(opdbId, "Godzilla (Premium)");
        var corpus = new SearchCorpusResult([
            // A metadata-card hit pointing at the SAME OPDB url as the record.
            SampleHit(documentId: $"meta_{opdbId}", documentUrl: sharedUrl,
                      machineId: opdbId, section: "Metadata",
                      pageStart: 0, pageEnd: 0,
                      score: expectedScore, lastScrapedUtc: expectedTs),
        ]);

        var dtoEl = JsonSerializer.SerializeToElement(dto, WebCamelCaseJson);
        var corpusEl = JsonSerializer.SerializeToElement(corpus, WebCamelCaseJson);

        var sink = new RetrievalCitationMetadataSink();
        sink.Record(sharedUrl, new RetrievalCitationMetadata(expectedTs, expectedScore));
        var extractor = new ToolTraceCitationExtractor(metadataSink: sink);

        var machineMsg = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_machine", dtoEl)]);
        var corpusMsg = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_corpus", corpusEl)]);
        var response = BuildAgentResponse(machineRecordFirst
            ? [machineMsg, corpusMsg]
            : [corpusMsg, machineMsg]);

        // Exactly one citation for the shared URL — and it is the RICHER
        // CorpusChunk carrying freshness + relevance, not the bare record.
        var citation = Assert.Single(extractor.Extract(response));
        Assert.Equal(CitationSourceType.CorpusChunk, citation.SourceType);
        Assert.Equal(sharedUrl, citation.SourceUrl);
        Assert.Equal(expectedTs, citation.LastScrapedUtc);
        Assert.Equal(expectedScore, citation.RelevanceScore);
    }

    // ── Task 5: ExtractWithSourceIndex exposes the k→SourceUrl table ────────
    // The reconciler needs to map [[cite:k]] markers to SourceUrl in the order
    // the model saw the searchCorpus hits. getMachineByTitle / OPDB-regex
    // citations go into Citations but NOT into SourceIndex (they are grounding
    // records, not numbered sources the model cites with [[cite:k]]).

    [Fact]
    public void ExtractWithSourceIndex_orders_searchCorpus_hits_by_tool_trace_appearance()
    {
        // Arrange: two sequential searchCorpus tool results.
        // First result returns urlA then urlB; second returns urlC.
        // SourceIndex must reflect the flattened order across both calls.
        var corpusA = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://a/1"),
            SampleHit(documentId: "doc_b", documentUrl: "https://b/1"),
        ]);
        var corpusB = new SearchCorpusResult([
            SampleHit(documentId: "doc_c", documentUrl: "https://c/1"),
        ]);

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_sc1", corpusA)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_sc2", corpusB)]));

        var (citations, sourceIndex) = new ToolTraceCitationExtractor().ExtractWithSourceIndex(response);

        // SourceIndex: k-1 → SourceUrl of the k-th searchCorpus hit in tool-trace order.
        string[] expectedIndex = ["https://a/1", "https://b/1", "https://c/1"];
        Assert.Equal(expectedIndex, sourceIndex);
        // Citations still contain all three corpus chunks.
        Assert.Equal(3, citations.Count);
    }

    [Fact]
    public void ExtractWithSourceIndex_excludes_getMachineByTitle_from_sourceIndex()
    {
        // getMachineByTitle citations are grounding records — they appear in
        // Citations but are NOT numbered sources, so they must not appear in
        // SourceIndex.
        var dto = SampleGroundingDto(opdbId: "GRBE-MJL05", title: "Godzilla (Premium)");
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf"),
        ]);

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_machine", dto)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_corpus", corpus)]));

        var (citations, sourceIndex) = new ToolTraceCitationExtractor().ExtractWithSourceIndex(response);

        // Only the corpus hit belongs in SourceIndex; the machine record does not.
        string[] expectedIndex = ["https://example/manual.pdf"];
        Assert.Equal(expectedIndex, sourceIndex);
        // Both citations are present (getMachineByTitle + searchCorpus).
        Assert.Equal(2, citations.Count);
    }

    [Fact]
    public void ExtractWithSourceIndex_nullResponse_returnsEmpty()
    {
        var (citations, sourceIndex) = new ToolTraceCitationExtractor().ExtractWithSourceIndex(null);

        Assert.Empty(citations);
        Assert.Empty(sourceIndex);
    }

    [Fact]
    public void ExtractWithSourceIndex_keeps_duplicate_urls_positional_while_citations_dedupe()
    {
        // Two searchCorpus hits share the SAME DocumentUrl but have different
        // DocumentIds (e.g. two differently-chunked segments from one hosted PDF
        // that was re-ingested under a second chunk ID). The k→SourceUrl table
        // (SourceIndex) is positional: both positions reference that URL so the
        // model's [[cite:1]] and [[cite:2]] both resolve. The Citations list is
        // deduped by SourceUrl → exactly one Citation for the shared URL.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://x/shared.pdf",
                      section: "Section A", pageStart: 1, pageEnd: 3),
            SampleHit(documentId: "doc_b", documentUrl: "https://x/shared.pdf",
                      section: "Section B", pageStart: 7, pageEnd: 9),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var (citations, sourceIndex) = new ToolTraceCitationExtractor().ExtractWithSourceIndex(response);

        // SourceIndex is positional: both hits, same URL, two entries.
        Assert.Equal(2, sourceIndex.Count);
        Assert.All(sourceIndex, u => Assert.Equal("https://x/shared.pdf", u));

        // Citations are deduped by SourceUrl: only one entry for the shared URL.
        Assert.Single(citations);
        Assert.Equal("https://x/shared.pdf", citations[0].SourceUrl);
    }

    [Fact]
    public void Extract_delegates_to_ExtractWithSourceIndex_and_returns_identical_citations()
    {
        // Extract(response) must behave identically to ExtractWithSourceIndex(response).Citations.
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/doc_a.pdf",
                      machineId: "GRBE-MJL05"),
            SampleHit(documentId: "doc_b", documentUrl: "https://example/doc_b.pdf",
                      machineId: "GRBE-MJL05"),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var viaDirect = Extractor.Extract(response);
        var (viaIndex, _) = Extractor.ExtractWithSourceIndex(response);

        Assert.Equal(viaDirect.Count, viaIndex.Count);
        for (var i = 0; i < viaDirect.Count; i++)
        {
            Assert.Equal(viaDirect[i].SourceUrl, viaIndex[i].SourceUrl);
            Assert.Equal(viaDirect[i].DocumentChunkId, viaIndex[i].DocumentChunkId);
        }
    }

    // ── getMarketValue (ADR-0045) — typed + JSON arms ──────────────────────
    // MarketValueDto carries AttributionUrl as the distinctive probe field.
    // Results go to Citations only (not SourceIndex), like getMachineByTitle.

    [Fact]
    public void Extract_MarketValueDto_AsTyped_ProducesCitation()
    {
        // Typed arm: real SDK path in unit tests.
        var dto = SampleMarketValueDto("MM5K-MRKPL");
        var response = BuildAgentResponseWithToolResult("getMarketValue", dto);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal("https://silverballlabs.com/market/MM5K-MRKPL", citation.SourceUrl);
        Assert.Equal(CitationSourceType.MarketValue, citation.SourceType);
        Assert.Null(citation.MachineId);         // no OPDB ID in market-value citations
        Assert.Null(citation.DocumentChunkId);
        Assert.Contains("Medieval Madness", citation.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_MarketValueDto_AsTyped_EmptyAttributionUrl_NoCitation()
    {
        // Defensive: no attribution URL → nothing to link → no citation.
        var dto = SampleMarketValueDto("ignored") with { AttributionUrl = "" };
        var response = BuildAgentResponseWithToolResult("getMarketValue", dto);

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_MarketValueDto_AsTyped_NullResult_NoCitation()
    {
        // Tool returned null (Silverball not configured or no data).
        var response = BuildAgentResponseWithToolResult("getMarketValue", null);

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void Extract_MarketValueDto_AsCamelCaseJsonElement_ProducesCitation()
    {
        // JSON arm (live Foundry path): AIFunctionFactory serializes with
        // JsonSerializerDefaults.Web (camelCase). The extractor probes for
        // "attributionUrl" to recognize the MarketValueDto shape.
        var dto = SampleMarketValueDto("MM5K-MRKPL");
        var element = JsonSerializer.SerializeToElement(dto, LiveCamelCaseJson);
        var response = BuildAgentResponseWithToolResult("getMarketValue", element);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal("https://silverballlabs.com/market/MM5K-MRKPL", citation.SourceUrl);
        Assert.Equal(CitationSourceType.MarketValue, citation.SourceType);
    }

    [Fact]
    public void Extract_MarketValueDto_AsJsonElement_EmptyAttributionUrl_NoCitation()
    {
        // The JSON probe finds "attributionUrl" but its value is empty →
        // AddCitationFromMarketValueDto should skip it.
        var dto = SampleMarketValueDto("ignored") with { AttributionUrl = "" };
        var element = JsonSerializer.SerializeToElement(dto, LiveCamelCaseJson);
        var response = BuildAgentResponseWithToolResult("getMarketValue", element);

        Assert.Empty(Extractor.Extract(response));
    }

    [Fact]
    public void ExtractWithSourceIndex_MarketValueDto_NotInSourceIndex()
    {
        // MarketValueDto → Citations only, NOT SourceIndex (same rule as
        // getMachineByTitle — it's not a [[cite:k]]-numbered corpus source).
        var dto = SampleMarketValueDto("MM5K-MRKPL");
        var response = BuildAgentResponseWithToolResult("getMarketValue", dto);

        var (citations, sourceIndex) = new ToolTraceCitationExtractor().ExtractWithSourceIndex(response);

        Assert.Single(citations);
        Assert.Empty(sourceIndex);
    }

    [Fact]
    public void Extract_GetMarketValue_AndSearchCorpus_Both_Surface()
    {
        // End-to-end shape: Wizard called getMarketValue AND searchCorpus
        // for the same machine. Both produce citations from different channels.
        var mvDto = SampleMarketValueDto("MM5K-MRKPL");
        var corpus = new SearchCorpusResult([
            SampleHit(documentId: "doc_a", documentUrl: "https://example/manual.pdf",
                      machineId: "MM5K-MRKPL"),
        ]);

        var response = BuildAgentResponse(
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_mv", mvDto)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_sc", corpus)]));

        var citations = Extractor.Extract(response);

        Assert.Equal(2, citations.Count);
        Assert.Contains(citations, c =>
            c.SourceUrl == "https://silverballlabs.com/market/MM5K-MRKPL" &&
            c.SourceType == CitationSourceType.MarketValue);
        Assert.Contains(citations, c =>
            c.SourceUrl == "https://example/manual.pdf" &&
            c.SourceType == CitationSourceType.CorpusChunk);
    }

    private static MarketValueDto SampleMarketValueDto(string opdbId) =>
        new(MachineTitle: "Medieval Madness",
            MedianPrice: 5500m,
            AvgPrice: 5600m,
            Min: 4500m,
            Max: 7000m,
            ByCondition: [new MarketValueConditionDto("excellent", 5500m, 15)],
            TrendDirection: "stable",
            PriceSummary: "Steady around $5,500.",
            LastSaleDate: "2026-06-01",
            AttributionUrl: $"https://silverballlabs.com/market/{opdbId}",
            AttributionText: "Powered by Silverball Labs");

    // ── ADR-0052: citation link target follows source knowledge-shape ────────
    //
    // Machine-derived structured-record projections (MetadataCard / GameOverview)
    // have no scraped_documents_raw row. Emitting them as CorpusChunk citations
    // produces a /documents/{id} link that 404s. They must be classified by
    // IsMachineDerivedStructuredRecord and emitted as MachineRecord citations so
    // CitationCard links to /machines/resolve/{MachineId} instead.

    // ── IsMachineDerivedStructuredRecord helper — direct unit tests ───────────

    [Theory]
    [InlineData("MetadataCard", true)]
    [InlineData("metadata_card", true)]
    [InlineData("GameOverview", true)]
    [InlineData("game_overview", true)]
    [InlineData("Manual", false)]
    [InlineData("manual", false)]
    [InlineData("ServiceBulletin", false)]
    [InlineData("Rulesheet", false)]   // other real-document types stay out of scope (guards against over-broadening)
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMachineDerivedStructuredRecord_classifies_expected_values(
        string? documentType,
        bool expectedResult)
    {
        Assert.Equal(
            expectedResult,
            ToolTraceCitationExtractor.IsMachineDerivedStructuredRecord(documentType));
    }

    // ── MetadataCard corpus hit → MachineRecord citation ─────────────────────

    [Fact]
    public void Extract_MetadataCardHit_PascalCase_EmitsMachineRecordCitation()
    {
        // A MetadataCard corpus hit (PascalCase, as stored in the AI Search index)
        // must produce a MachineRecord citation with MachineId set and
        // DocumentChunkId null — so CitationCard links to /machines/resolve/{id}
        // instead of /documents/{id} (which 404s for structured-record projections).
        const string opdbUrl = "https://opdb.org/search?q=GRBE-MJL05";
        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "GRBE-MJL05",
                MachineTitle: "Godzilla (Premium)",
                DocumentId: "meta_GRBE-MJL05",
                DocumentUrl: opdbUrl,
                DocumentType: "MetadataCard",
                PageStart: 0,
                PageEnd: 0,
                SectionHeading: "Metadata",
                Content: "Godzilla (Premium) by Stern, 2021."),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
        Assert.Null(citation.DocumentChunkId);   // no /documents link
        Assert.Equal(opdbUrl, citation.SourceUrl);
    }

    [Fact]
    public void Extract_MetadataCardHit_SnakeCase_EmitsMachineRecordCitation()
    {
        // Same as above but with the snake_case alias form ("metadata_card")
        // used on the filter side. Both forms must classify identically.
        const string opdbUrl = "https://opdb.org/search?q=GRBE-MJL05";
        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "GRBE-MJL05",
                MachineTitle: "Godzilla (Premium)",
                DocumentId: "meta_GRBE-MJL05",
                DocumentUrl: opdbUrl,
                DocumentType: "metadata_card",
                PageStart: 0,
                PageEnd: 0,
                SectionHeading: "Metadata",
                Content: "Godzilla (Premium) by Stern, 2021."),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Equal("GRBE-MJL05", citation.MachineId);
        Assert.Null(citation.DocumentChunkId);
    }

    // ── GameOverview corpus hit → MachineRecord citation ─────────────────────

    [Fact]
    public void Extract_GameOverviewHit_PascalCase_EmitsMachineRecordCitation()
    {
        // GameOverview corpus hits are also machine-derived projections.
        const string opdbUrl = "https://opdb.org/search?q=Gj66Z-Mp4BN";
        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "Gj66Z-Mp4BN",
                MachineTitle: "Halloween (Pro)",
                DocumentId: "overview_Gj66Z-Mp4BN",
                DocumentUrl: opdbUrl,
                DocumentType: "GameOverview",
                PageStart: 0,
                PageEnd: 0,
                SectionHeading: "Overview",
                Content: "Halloween Pro overview prose."),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Equal("Gj66Z-Mp4BN", citation.MachineId);
        Assert.Null(citation.DocumentChunkId);
        Assert.Equal(opdbUrl, citation.SourceUrl);
    }

    [Fact]
    public void Extract_GameOverviewHit_SnakeCase_EmitsMachineRecordCitation()
    {
        // snake_case alias "game_overview" must classify identically to "GameOverview".
        const string opdbUrl = "https://opdb.org/search?q=Gj66Z-Mp4BN";
        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "Gj66Z-Mp4BN",
                MachineTitle: "Halloween (Pro)",
                DocumentId: "overview_Gj66Z-Mp4BN",
                DocumentUrl: opdbUrl,
                DocumentType: "game_overview",
                PageStart: 0,
                PageEnd: 0,
                SectionHeading: "Overview",
                Content: "Halloween Pro overview prose."),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Equal("Gj66Z-Mp4BN", citation.MachineId);
        Assert.Null(citation.DocumentChunkId);
    }

    // ── Unstructured-text document hits remain CorpusChunk ───────────────────

    [Fact]
    public void Extract_ManualHit_RemainsCorpusChunk_NotOverRouted()
    {
        // A Manual (real document) corpus hit must be emitted unchanged as
        // CorpusChunk with DocumentChunkId set — it has a scraped_documents_raw
        // row and its /documents/{id} link is live. Guards against over-routing.
        const string pdfUrl = "https://sternpinball.com/manuals/godzilla_manual.pdf";
        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "GRBE-MJL05",
                MachineTitle: "Godzilla (Premium)",
                DocumentId: "doc_abc123",
                DocumentUrl: pdfUrl,
                DocumentType: "Manual",
                PageStart: 12,
                PageEnd: 14,
                SectionHeading: "Coil Replacement",
                Content: "Replace the coil as follows..."),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.CorpusChunk, citation.SourceType);
        Assert.Equal("doc_abc123", citation.DocumentChunkId);  // /documents link stays
        Assert.Equal("GRBE-MJL05", citation.MachineId);
        Assert.Equal(12, citation.PageStart);
        Assert.Equal("Coil Replacement", citation.SectionHeading);
    }

    // ── Defensive: structured-record hit with blank MachineId ─────────────────

    [Fact]
    public void Extract_MetadataCardHit_BlankMachineId_FallsBackToCorpusChunk()
    {
        // A MetadataCard hit with no MachineId (not expected in practice) must
        // degrade to corpus-chunk shape rather than dropping the citation entirely.
        // Better a slightly-wrong link than silence — invariant #17.
        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "",
                MachineTitle: "Unknown",
                DocumentId: "meta_orphan",
                DocumentUrl: "https://opdb.org/search?q=UNKNOWN",
                DocumentType: "MetadataCard",
                PageStart: 0,
                PageEnd: 0,
                SectionHeading: "",
                Content: "Orphaned metadata."),
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        // Falls back to corpus-chunk shape (not dropped, not null).
        Assert.Equal(CitationSourceType.CorpusChunk, citation.SourceType);
        Assert.Equal("meta_orphan", citation.DocumentChunkId);
    }

    // ── Thread-through: RelevanceScore and LastScrapedUtc flow onto machine citations ──

    [Fact]
    public void Extract_MetadataCardHit_ThreadsRelevanceScoreAndFreshness()
    {
        // MachineRecord citations emitted from MetadataCard hits must carry
        // RelevanceScore and LastScrapedUtc from the two-channel pattern, same
        // as corpus-chunk citations, so the CitationCard freshness badge and
        // match-percent badge render correctly.
        var expectedTs = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        const double expectedScore = 3.2;
        const string opdbUrl = "https://opdb.org/search?q=GRBE-MJL05";

        var corpus = new SearchCorpusResult([
            new SearchCorpusHit(
                MachineId: "GRBE-MJL05",
                MachineTitle: "Godzilla (Premium)",
                DocumentId: "meta_GRBE-MJL05",
                DocumentUrl: opdbUrl,
                DocumentType: "MetadataCard",
                PageStart: 0,
                PageEnd: 0,
                SectionHeading: "Metadata",
                Content: "Godzilla by Stern.")
            {
                Score = expectedScore,
                LastScrapedUtc = expectedTs,
            },
        ]);
        var response = BuildAgentResponseWithToolResult("searchCorpus", corpus);

        var citation = Assert.Single(Extractor.Extract(response));

        Assert.Equal(CitationSourceType.MachineRecord, citation.SourceType);
        Assert.Equal(expectedScore, citation.RelevanceScore);
        Assert.Equal(expectedTs, citation.LastScrapedUtc);
    }

    // Simple capturing logger for Warning-log assertions.
    private sealed class CapturingExtractorLogger : ILogger<ToolTraceCitationExtractor>
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                WarningCount++;
            }
        }
    }
}
