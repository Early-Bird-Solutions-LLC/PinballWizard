using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Hosting;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Observability;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Tools;

// Behavior-asserting tests for SearchCorpusTool (build-spec § Phase 4
// item 21, ADR-0014 + ADR-0022). The retriever is mocked via NSubstitute
// to keep tests pure; the live integration path is exercised by the
// gated `LiveSearchCorpusToolTests` against a deployed AI Search index.
public sealed class SearchCorpusToolTests
{
    private static SearchCorpusTool NewTool(
        IRagRetriever retriever,
        IDegradationContext? degradationContext = null,
        IRuntimeSettings? runtimeSettings = null) =>
        new(retriever,
            degradationContext ?? new AmbientDegradationContext(),
            NullLogger<SearchCorpusTool>.Instance,
            runtimeSettings);

    private static readonly DateTimeOffset SampleLastScraped =
        new(2026, 3, 22, 14, 30, 0, TimeSpan.Zero);

    private static RetrievedChunk SampleChunk(
        string chunkId = "chk_1",
        string machineId = "GRBE-MJL05",
        string documentId = "doc_1",
        int pageStart = 1,
        int pageEnd = 1) =>
        new(
            ChunkId: chunkId,
            MachineId: machineId,
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern Pinball",
            DocumentId: documentId,
            DocumentUrl: $"https://example/{documentId}.pdf",
            DocumentType: "manual",
            PageStart: pageStart,
            PageEnd: pageEnd,
            SectionHeading: "Foo Mode",
            Content: "Foo Mode rules text…",
            Score: 0.85,
            LastScrapedUtc: SampleLastScraped,
            Edition: "Premium",
            EditionScope: "single-edition");

    [Fact]
    public async Task SearchCorpusAsync_WhitespaceQuery_ReturnsEmptyWithoutCallingRetriever()
    {
        // Empty-query short-circuit prevents the model from looping
        // when a confused prompt edit produces "" — and keeps the
        // counter clean (no retrieval was attempted, not a retrieval
        // that returned zero).
        var retriever = Substitute.For<IRagRetriever>();
        var tool = NewTool(retriever);

        var result = await tool.SearchCorpusAsync(
            query: "   ",
            machineId: null,
            documentType: null,
            topK: null,
            cancellationToken: CancellationToken.None);

        Assert.Empty(result.Hits);
        await retriever.DidNotReceive().RetrieveAsync(
            Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_NullArgs_PassThroughAsUnfilteredRetrieval()
    {
        // The model can omit machineId / documentType / topK; the tool
        // builds RetrievalOptions with defaults so the retriever sees
        // no filter at all.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = NewTool(retriever);
        await tool.SearchCorpusAsync(
            query: "godzilla coil resistance",
            machineId: null,
            documentType: null,
            topK: null,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            "godzilla coil resistance",
            Arg.Is<RetrievalOptions>(o =>
                o.MachineId == null
                && o.DocumentType == null
                && o.TopK == SearchCorpusTool.TopKDefault),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_PassesArgsThroughToRetrievalOptions()
    {
        // documentType is normalized from prompt-friendly snake_case to
        // the indexed PascalCase before reaching the retriever.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = NewTool(retriever);
        await tool.SearchCorpusAsync(
            query: "service bulletin",
            machineId: "GRBE-MJL05",
            documentType: "service_bulletin",
            topK: 3,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            "service bulletin",
            Arg.Is<RetrievalOptions>(o =>
                o.MachineId == "GRBE-MJL05"
                && o.DocumentType == "ServiceBulletin"
                && o.TopK == 3),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("manual", "Manual")]
    [InlineData("service_bulletin", "ServiceBulletin")]
    [InlineData("metadata_card", "MetadataCard")]
    [InlineData("MANUAL", "Manual")]
    [InlineData("SERVICE_BULLETIN", "ServiceBulletin")]
    [InlineData("Manual", "Manual")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("unknown_type", "unknown_type")]
    public void NormalizeDocumentType_MapsPromptValuesToIndexedForm(string? input, string? expected)
    {
        // The AI Search index stores document_type as DocumentType enum's
        // .ToString() (PascalCase). Wizard prompt uses lowercase snake_case
        // aliases. NormalizeDocumentType bridges the contract so OData
        // filter eq clauses match the indexed values.
        Assert.Equal(expected, SearchCorpusTool.NormalizeDocumentType(input));
    }

    [Fact]
    public async Task SearchCorpusAsync_EmptyStringFilters_NormalizeToNull()
    {
        // Empty string is "model didn't supply" semantics — must not
        // emit `eq ''` filter clauses that would exclude every legit
        // value. AiSearchRagRetriever.BuildFilter applies the same
        // empty-as-absent rule on its end; both sides agree.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = NewTool(retriever);
        await tool.SearchCorpusAsync(
            query: "x",
            machineId: "  ",
            documentType: "",
            topK: null,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.MachineId == null && o.DocumentType == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, SearchCorpusTool.TopKDefault)]
    [InlineData(0, SearchCorpusTool.TopKDefault)]
    [InlineData(-5, SearchCorpusTool.TopKDefault)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, SearchCorpusTool.TopKCeiling)]
    [InlineData(1000, SearchCorpusTool.TopKCeiling)]
    public void ClampTopK_HonorsCeilingAndDefaults(int? requested, int expected)
    {
        Assert.Equal(expected, SearchCorpusTool.ClampTopK(requested));
    }

    [Fact]
    public async Task SearchCorpusAsync_MapsRetrievedChunksToHits_PreservingFields()
    {
        // ChunkId and Manufacturer are dropped (no model-facing value).
        // Score is threaded through [JsonIgnore] (PR-C2) so it lands on
        // SearchCorpusHit.Score for the citation extractor, but the model
        // never sees it in the JSON payload. All other fields flow through
        // unchanged for MachineTitle / DocumentUrl / page range /
        // SectionHeading / Content.
        var chunk = SampleChunk(chunkId: "chk_abc", documentId: "doc_x", pageStart: 42, pageEnd: 43);
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([chunk]);

        var tool = NewTool(retriever);
        var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(chunk.MachineId, hit.MachineId);
        Assert.Equal(chunk.MachineTitle, hit.MachineTitle);
        Assert.Equal(chunk.DocumentId, hit.DocumentId);
        Assert.Equal(chunk.DocumentUrl, hit.DocumentUrl);
        Assert.Equal(chunk.DocumentType, hit.DocumentType);
        Assert.Equal(42, hit.PageStart);
        Assert.Equal(43, hit.PageEnd);
        Assert.Equal(chunk.SectionHeading, hit.SectionHeading);
        Assert.Equal(chunk.Content, hit.Content);
        // Score is threaded through [JsonIgnore] — visible to C# code.
        Assert.Equal(chunk.Score, hit.Score);
        // PR-C3: LastScrapedUtc is threaded through [JsonIgnore] — visible to C# code.
        Assert.Equal(chunk.LastScrapedUtc, hit.LastScrapedUtc);
        // Task 7 (AB#259): Edition + EditionScope are model-VISIBLE — they
        // flow through to the hit so the model can decide R1/R2/R3.
        Assert.Equal(chunk.Edition, hit.Edition);
        Assert.Equal(chunk.EditionScope, hit.EditionScope);
    }

    [Fact]
    public async Task SearchCorpusAsync_ReturnsAllChunksFromRetriever_WithoutDedup()
    {
        // De-duplication of citations happens in the citation extractor
        // (one Citation per unique DocumentId); the tool itself returns
        // every chunk so the model can read both as evidence.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([
                SampleChunk(chunkId: "chk_a", documentId: "doc_x"),
                SampleChunk(chunkId: "chk_b", documentId: "doc_x"), // same doc
                SampleChunk(chunkId: "chk_c", documentId: "doc_y"),
            ]);

        var tool = NewTool(retriever);
        var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);

        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task SearchCorpusAsync_RetrieverThrows_ReturnsEmpty_DoesNotPropagate()
    {
        // ADR-0023 negative-consequence #3: tool-side failures must NOT
        // bubble out of the function call. Microsoft Agent Framework
        // would retry on a thrown exception, looping the model. Empty
        // result lets the citation-required guardrail (W4-3) surface
        // a NoCitation refusal cleanly.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ =>
                throw new InvalidOperationException("simulated AI Search outage"));

        var tool = NewTool(retriever);
        var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SearchCorpusAsync_RetrieverCancels_PropagatesCancellation()
    {
        // Cancellation is the caller's intent — must NOT be swallowed.
        // The exception filter in SearchCorpusTool catches
        // OperationCanceledException explicitly to re-throw rather than
        // burying it under "empty result".
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ =>
                throw new OperationCanceledException());

        var tool = NewTool(retriever);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None));
    }

    [Fact]
    public void Ctor_NullRetriever_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchCorpusTool(null!, new AmbientDegradationContext(), NullLogger<SearchCorpusTool>.Instance));
    }

    // ── pinwiz.ai.tool_duration_ms emission ──────────────────────────────
    // The tool wraps Stopwatch.StartNew + try/finally around its body so
    // every invocation (success, empty result, transport-error catch path)
    // produces exactly one tool_duration_ms sample tagged tool=searchCorpus.
    // Failure latency is operationally meaningful — slow-then-empty and
    // fast-then-empty need different alerts — so the test asserts emission
    // on both the success path and the catch path.

    [Fact]
    public async Task SearchCorpusAsync_Success_EmitsToolDurationMs_TaggedSearchCorpus()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([SampleChunk()]);

        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = NewTool(retriever);
            await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);
        }

        AssertOurToolEmittedAtLeastOnce(samples);
    }

    [Fact]
    public async Task SearchCorpusAsync_RetrieverThrows_StillEmitsToolDurationMs()
    {
        // The empty-result fail-closed posture (ADR-0023 § Negative
        // consequence #3) returns from the catch block. Stopwatch must
        // still close in the outer finally so operators can observe
        // failure latency, not just success latency.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ =>
                throw new InvalidOperationException("simulated AI Search outage"));

        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = NewTool(retriever);
            var result = await tool.SearchCorpusAsync("q", null, null, null, CancellationToken.None);
            Assert.Empty(result.Hits);
        }

        AssertOurToolEmittedAtLeastOnce(samples);
    }

    [Fact]
    public async Task SearchCorpusAsync_WhitespaceQuery_DoesNotEmitToolDurationMs()
    {
        // Empty-query short-circuit fires BEFORE the Stopwatch starts, so
        // dashboards don't see noise samples for prompts the orchestrator
        // accidentally produced. This is the stated design — keep it
        // pinned so a future refactor doesn't quietly add a cardinality
        // dimension to the no-op path.
        var retriever = Substitute.For<IRagRetriever>();
        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = NewTool(retriever);
            await tool.SearchCorpusAsync("   ", null, null, null, CancellationToken.None);
        }

        Assert.DoesNotContain(samples, s => s.ToolTag == SearchCorpusTool.ToolTagValue);
    }

    // The instrument is a single process-global Meter, so emissions from
    // MachineGroundingToolTests running in parallel with this class land
    // in our listener too. We assert only on emissions tagged with this
    // tool's name — that's what the test actually cares about. We also
    // do NOT assert sample count == 1, because in parallel test runs
    // the same tool may emit again from concurrent test executions; the
    // emission *contract* is "tool fires this metric on every call" and
    // a Contains assertion captures that without coupling to scheduler.
    private static void AssertOurToolEmittedAtLeastOnce(
        IEnumerable<(double Value, string? ToolTag)> samples)
    {
        Assert.Contains(
            samples,
            s => s.ToolTag == SearchCorpusTool.ToolTagValue && s.Value >= 0);
    }

    private static ConcurrentBag<(double Value, string? ToolTag)> CollectToolDurationSamples(out MeterListener listener)
    {
        // Force `PinballWizardTelemetry`'s static cctor to complete first
        // so the instrument exists when we wire the listener. Explicitly
        // enabling the named instrument after `Start()` is more
        // deterministic than the `InstrumentPublished` delivery path.
        // ConcurrentBag is required because parallel test classes that
        // emit to the same process-global Meter cause concurrent
        // measurement callbacks on this listener.
        var samples = new ConcurrentBag<(double Value, string? ToolTag)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? toolTag = null;
            foreach (var t in tags)
            {
                if (t.Key == "tool")
                {
                    toolTag = t.Value as string;
                }
            }
            samples.Add((value, toolTag));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.AiToolDurationMs);
        listener = l;
        return samples;
    }
    // ─────────────────────────────────────────────────────────────────────
    // PR-D2: SearchUnavailable degradation tests
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullDegradationContext_Throws()
    {
        var retriever = Substitute.For<IRagRetriever>();
        Assert.Throws<ArgumentNullException>(() =>
            new SearchCorpusTool(retriever, null!, NullLogger<SearchCorpusTool>.Instance));
    }

    [Fact]
    public void IsTimeoutCancellation_AlreadyCancelledToken_ReturnsTrue()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var oce = new OperationCanceledException(cts.Token);
        Assert.True(SearchCorpusTool.IsTimeoutCancellation(oce));
    }

    [Fact]
    public void IsTimeoutCancellation_DefaultToken_ReturnsFalse()
    {
        var oce = new OperationCanceledException();
        Assert.False(SearchCorpusTool.IsTimeoutCancellation(oce));
    }

    [Fact]
    public async Task SearchCorpusAsync_On5xxRequestFailed_MarksSearchUnavailable_ReturnsEmpty()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new RequestFailedException(503, "Service Unavailable"));

        var ctx = Substitute.For<IDegradationContext>();
        var tool = NewTool(retriever, ctx);

        var result = await tool.SearchCorpusAsync("any", null, null, null, CancellationToken.None);

        Assert.Empty(result.Hits);
        ctx.Received(1).Mark(DegradationMode.SearchUnavailable, Arg.Any<string>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task SearchCorpusAsync_On5xxRequestFailed_EmitsSearchUnavailableCounter_TaggedHttp5xx()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new RequestFailedException(500, "Internal Server Error"));

        var tool = NewTool(retriever);
        var samples = CollectSearchUnavailableSamples(out var listener);

        await tool.SearchCorpusAsync("query", null, null, null, CancellationToken.None);

        listener.Dispose();
        Assert.Contains(samples, s => s.ReasonTag == "http_5xx");
    }

    [Fact]
    public async Task SearchCorpusAsync_OnAuthFailure_MarksSearchUnavailable_ReturnsEmpty()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new AuthenticationFailedException("Token expired"));

        var ctx = Substitute.For<IDegradationContext>();
        var tool = NewTool(retriever, ctx);

        var result = await tool.SearchCorpusAsync("any", null, null, null, CancellationToken.None);

        Assert.Empty(result.Hits);
        ctx.Received(1).Mark(DegradationMode.SearchUnavailable, Arg.Any<string>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task SearchCorpusAsync_OnAuthFailure_EmitsSearchUnavailableCounter_TaggedAuthFailure()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new AuthenticationFailedException("Token expired"));

        var tool = NewTool(retriever);
        var samples = CollectSearchUnavailableSamples(out var listener);

        await tool.SearchCorpusAsync("query", null, null, null, CancellationToken.None);

        listener.Dispose();
        Assert.Contains(samples, s => s.ReasonTag == "auth_failure");
    }

    [Fact]
    public async Task SearchCorpusAsync_OnTimeout_MarksSearchUnavailable_ReturnsEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var internalToken = cts.Token;

        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new OperationCanceledException("Timeout", internalToken));

        var ctx = Substitute.For<IDegradationContext>();
        var tool = NewTool(retriever, ctx);

        var result = await tool.SearchCorpusAsync("any", null, null, null, CancellationToken.None);

        Assert.Empty(result.Hits);
        ctx.Received(1).Mark(DegradationMode.SearchUnavailable, Arg.Any<string>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task SearchCorpusAsync_OnTimeout_EmitsSearchUnavailableCounter_TaggedTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var internalToken = cts.Token;

        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new OperationCanceledException("Timeout", internalToken));

        var tool = NewTool(retriever);
        var samples = CollectSearchUnavailableSamples(out var listener);

        await tool.SearchCorpusAsync("query", null, null, null, CancellationToken.None);

        listener.Dispose();
        Assert.Contains(samples, s => s.ReasonTag == "timeout");
    }

    [Fact]
    public async Task SearchCorpusAsync_OtherException_MarksSearchUnavailable_ReturnsEmpty()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new InvalidOperationException("Unexpected failure"));

        var ctx = Substitute.For<IDegradationContext>();
        var tool = NewTool(retriever, ctx);

        var result = await tool.SearchCorpusAsync("any", null, null, null, CancellationToken.None);

        Assert.Empty(result.Hits);
        ctx.Received(1).Mark(DegradationMode.SearchUnavailable, Arg.Any<string>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task SearchCorpusAsync_OtherException_EmitsSearchUnavailableCounter_TaggedOther()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new InvalidOperationException("Unexpected failure"));

        var tool = NewTool(retriever);
        var samples = CollectSearchUnavailableSamples(out var listener);

        await tool.SearchCorpusAsync("query", null, null, null, CancellationToken.None);

        listener.Dispose();
        Assert.Contains(samples, s => s.ReasonTag == "other");
    }

    [Fact]
    public async Task SearchCorpusAsync_Success_DoesNotMarkDegradation()
    {
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RetrievedChunk>>([SampleChunk()]));

        var ctx = Substitute.For<IDegradationContext>();
        var tool = NewTool(retriever, ctx);

        await tool.SearchCorpusAsync("query", null, null, null, CancellationToken.None);

        // Success path: Mark() must not be called.
        ctx.DidNotReceive().Mark(Arg.Any<DegradationMode>(), Arg.Any<string?>(), Arg.Any<int?>());
    }

    private static ConcurrentBag<(long Value, string? ReasonTag)> CollectSearchUnavailableSamples(out MeterListener listener)
    {
        _ = PinballWizardTelemetry.AiSearchUnavailable; // Ensure instrument exists
        var samples = new ConcurrentBag<(long Value, string? ReasonTag)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reasonTag = null;
            foreach (var t in tags)
            {
                if (t.Key == "reason")
                {
                    reasonTag = t.Value as string;
                }
            }
            samples.Add((value, reasonTag));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.AiSearchUnavailable);
        listener = l;
        return samples;
    }

    // ─────────────────────────────────────────────────────────────────────
    // PR retrieval-runtime-keys: runtime settings consumer tests.
    //
    // These tests prove that the runtime-mutable rag.retrieval_top_k and
    // rag.retrieval_minimum_score values ACTUALLY reach the RetrievalOptions
    // passed to IRagRetriever.RetrieveAsync — not just that the snapshot is
    // constructed correctly (that's covered in RuntimeSettingsTests).
    // ─────────────────────────────────────────────────────────────────────

    private static IRuntimeSettings FakeSettings(int topK, double minimumScore)
    {
        // Build a snapshot with the given retrieval values and route it
        // through an NSubstitute fake so the tool calls GetSnapshotAsync.
        var snapshot = new RuntimeSettingsSnapshot(
            ConfidenceThreshold: 0.65,
            PerCallCostCeilingUsdCents: 10,
            MaxConversationTurns: 8,
            RetrievalTopK: topK,
            RetrievalMinimumScore: minimumScore);

        var rt = Substitute.For<IRuntimeSettings>();
        rt.GetSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(snapshot));
        return rt;
    }

    [Fact]
    public async Task SearchCorpusAsync_WithRuntimeTopKOverride_PassesOverriddenTopKToRetriever()
    {
        // The whole point of rag.retrieval_top_k: an overridden value must
        // land on RetrievalOptions.TopK when the model does not supply topK
        // (null). Without this wiring the key would be dead config.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RetrievedChunk>>([]));

        var tool = NewTool(retriever, runtimeSettings: FakeSettings(topK: 15, minimumScore: 0.0));

        await tool.SearchCorpusAsync(
            query: "godzilla flipper",
            machineId: null,
            documentType: null,
            topK: null, // model did not supply — runtime default applies
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 15),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_WithRuntimeMinimumScoreOverride_PassesOverriddenScoreToRetriever()
    {
        // The whole point of rag.retrieval_minimum_score: an overridden value
        // must land on RetrievalOptions.MinimumScore. Without this wiring the
        // post-filter in AiSearchRagRetriever would never see the stored value.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RetrievedChunk>>([]));

        var tool = NewTool(retriever, runtimeSettings: FakeSettings(topK: 10, minimumScore: 0.45));

        await tool.SearchCorpusAsync(
            query: "godzilla flipper",
            machineId: null,
            documentType: null,
            topK: null,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.MinimumScore == 0.45),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_ModelTopK_WinsOverRuntimeDefault()
    {
        // When the model explicitly requests topK=3, the runtime default
        // is irrelevant — the model's choice wins (clamped to TopKCeiling).
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RetrievedChunk>>([]));

        var tool = NewTool(retriever, runtimeSettings: FakeSettings(topK: 15, minimumScore: 0.0));

        await tool.SearchCorpusAsync(
            query: "godzilla flipper",
            machineId: null,
            documentType: null,
            topK: 3, // explicit — must not be replaced by the runtime default
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCorpusAsync_NoRuntimeSettings_UsesTopKDefaultConstant()
    {
        // Without IRuntimeSettings the tool falls back to the hardcoded
        // TopKDefault constant — same behavior as before PR retrieval-runtime-keys.
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RetrievedChunk>>([]));

        var tool = NewTool(retriever); // no runtimeSettings

        await tool.SearchCorpusAsync(
            query: "godzilla flipper",
            machineId: null,
            documentType: null,
            topK: null,
            cancellationToken: CancellationToken.None);

        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o =>
                o.TopK == SearchCorpusTool.TopKDefault
                && o.MinimumScore == new RetrievalOptions().MinimumScore),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, SearchCorpusTool.TopKDefault)]     // runtime default used
    [InlineData(0, SearchCorpusTool.TopKDefault)]         // ≤ 0 → runtime default
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, SearchCorpusTool.TopKCeiling)]        // clamped to ceiling
    public void ClampTopK_WithRuntimeDefault_HonorsBothDefaultAndCeiling(int? requested, int expected)
    {
        // Verify ClampTopK's runtimeDefault overload: when requested is
        // null / ≤ 0 the runtimeDefault applies, but the ceiling still
        // wins when the default itself exceeds it.
        Assert.Equal(expected, SearchCorpusTool.ClampTopK(requested, runtimeDefault: SearchCorpusTool.TopKDefault));
    }

    [Fact]
    public void ClampTopK_RuntimeDefaultAboveCeiling_IsClamped()
    {
        // A runtime-stored value of 25 is rejected by TryValidate (the
        // write guard enforces the 1–20 range), so this path should not
        // occur in practice. The ceiling clamp is still present so a
        // Data Explorer edit cannot produce a TopK that bypasses the
        // server-side limit.
        Assert.Equal(SearchCorpusTool.TopKCeiling, SearchCorpusTool.ClampTopK(null, runtimeDefault: 25));
    }
}
