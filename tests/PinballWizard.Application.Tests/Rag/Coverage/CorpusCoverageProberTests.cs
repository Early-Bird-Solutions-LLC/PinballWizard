using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Rag.Coverage;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Coverage;

public sealed class CorpusCoverageProberTests
{
    private static RetrievedChunk Chunk(string documentId, string manufacturer, string docType) =>
        new(ChunkId: "c1", MachineId: "m1", MachineTitle: "T", Manufacturer: manufacturer,
            DocumentId: documentId, DocumentUrl: "u", DocumentType: docType,
            PageStart: 1, PageEnd: 1, SectionHeading: "H", Content: "x", Score: 1.0);

    [Fact]
    public async Task Cell_WhoseSampleContentIsRetrievable_IsCovered()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == "kineticist_tutorials");
        index.CountAsync(kin, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(kin, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(kin, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("kineticist_godzilla_GRBN", "Stern", "Rulesheet", "Godzilla", "Wizard Mode"));
        // Every other source: empty + not expected, so no gaps from them.
        index.CountAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>()).Returns(0L);

        RetrievalOptions? capturedOptions = null;
        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions>(o => capturedOptions = o), Arg.Any<CancellationToken>())
                 .Returns([Chunk("kineticist_godzilla_GRBN", "Stern", "Rulesheet")]);

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "kineticist_tutorials" && c.DocumentType == "Rulesheet");
        Assert.True(cell.Retrievable);
        Assert.Equal("Godzilla Wizard Mode", cell.Query);
        Assert.Empty(report.Warnings);

        // Retrieval must be scoped to the cell's doc_type; kineticist is a synthesized
        // (zero-manufacturer-value) source so Manufacturer must be null.
        Assert.NotNull(capturedOptions);
        Assert.Equal("Rulesheet", capturedOptions!.DocumentType);
        Assert.Null(capturedOptions.Manufacturer);
    }

    [Fact]
    public async Task Cell_WhoseContentIsNotInRetrieval_IsARetrievabilityWarning_NotAGap()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == "kineticist_tutorials");
        index.CountAsync(kin, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(kin, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(kin, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("kineticist_godzilla_GRBN", "Stern", "Rulesheet", "Godzilla", "Wizard Mode"));
        // All other sources: 1 chunk (no source-floor gaps) + empty doc-type facets (no cells generated).
        index.CountAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>()).Returns(1L);
        index.FacetDocumentTypesAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<DocTypeCount>>([]);

        var retriever = Substitute.For<IRagRetriever>();
        // Retrieval returns a DIFFERENT source's chunk (same doc_type, wrong source prefix) so
        // the miss is caused solely by the source-recognizer mismatch (source.MatchesRetrieval half),
        // not by a document_type difference — isolating the AND condition under test.
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("doc_other", "Stern", "Rulesheet")]);

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "kineticist_tutorials");
        Assert.False(cell.Retrievable);
        // Unretrievable cell is a soft warning, not a hard gap.
        Assert.Contains(report.Warnings, c => c.Source == "kineticist_tutorials");
        Assert.Equal(1, report.RetrievabilityWarnings);
        Assert.Equal(0, report.GapsTotal); // no source-floor gaps in this fixture
    }

    [Fact]
    public async Task ExpectedNonEmptySource_WithZeroChunks_IsASourceGap()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        index.CountAsync(Arg.Any<RagSource>(), Arg.Any<CancellationToken>()).Returns(0L);
        var retriever = Substitute.For<IRagRetriever>();

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        Assert.Contains(report.SourceGaps, s => s.Source == "stern");        // ExpectedNonEmpty
        Assert.DoesNotContain(report.SourceGaps, s => s.Source == "twip");   // not ExpectedNonEmpty
        Assert.True(report.GapsTotal >= 1);                                   // source-floor gaps are hard gaps
    }

    [Fact]
    public async Task RetrievalThrows_RecordsCellAsNotRetrievable_WithError_DoesNotThrow()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == "kineticist_tutorials");
        index.CountAsync(kin, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(kin, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(kin, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("kineticist_x", "Stern", "Rulesheet", "Godzilla", "Wizard Mode"));
        index.CountAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new InvalidOperationException("search down"));

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "kineticist_tutorials");
        Assert.False(cell.Retrievable);
        Assert.NotNull(cell.Error);
    }

    // ── MatchesRetrieval / Option-A fix (Issue #842) ───────────────────────

    /// <summary>
    /// Regression guard for #842: a manufacturer-backed source (JJP) whose
    /// retrieval results are TiltForums/Kineticist chunks (non-doc_ prefix)
    /// must still report Retrievable=true, because the Wizard returns those
    /// chunks to users — the probe was producing a false-positive warning.
    /// </summary>
    [Fact]
    public async Task ManufacturerBackedSource_RetrievableOnlyViaNonDocChunks_IsReportedRetrievable()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var jjp = RagSourceCatalog.All.Single(s => s.SourceId == "jjp");

        // JJP has indexed content; sample returns a native doc_ chunk.
        index.CountAsync(jjp, Arg.Any<CancellationToken>()).Returns(10L);
        index.FacetDocumentTypesAsync(jjp, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 10)]);
        index.SampleAsync(jjp, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("doc_ca1982759f290833", "Jersey Jack Pinball",
                 "Rulesheet", "Elton John", "Rules"));
        // All other sources: empty + not expected, so no gaps.
        index.CountAsync(Arg.Is<RagSource>(s => s != jjp), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        // Retrieval returns ONLY a non-doc_ chunk (TiltForums-style) with the
        // correct manufacturer — this is the false-positive scenario from #842.
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("tiltforums_elton_john_abc123", "Jersey Jack Pinball", "Rulesheet")]);

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "jjp" && c.DocumentType == "Rulesheet");
        Assert.True(cell.Retrievable); // was false before the fix
        Assert.Empty(report.Warnings);
    }

    /// <summary>
    /// Genuine absence still fails: if retrieval returns chunks for a different
    /// manufacturer, the cell must still be Retrievable=false so real gaps are
    /// not hidden by the fix.
    /// </summary>
    [Fact]
    public async Task ManufacturerBackedSource_WithNoMatchingManufacturerInRetrieval_IsStillNotRetrievable()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var jjp = RagSourceCatalog.All.Single(s => s.SourceId == "jjp");

        index.CountAsync(jjp, Arg.Any<CancellationToken>()).Returns(10L);
        index.FacetDocumentTypesAsync(jjp, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 10)]);
        index.SampleAsync(jjp, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("doc_ca1982759f290833", "Jersey Jack Pinball",
                 "Rulesheet", "Elton John", "Rules"));
        index.CountAsync(Arg.Is<RagSource>(s => s != jjp), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        // All top-10 results belong to a different manufacturer — genuine retrieval gap.
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("doc_stern_foo", "Stern", "Rulesheet")]);

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "jjp" && c.DocumentType == "Rulesheet");
        Assert.False(cell.Retrievable);
        Assert.Contains(report.Warnings, c => c.Source == "jjp");
    }

    /// <summary>
    /// The sampling path must still pass the source with its DocumentIdPrefix
    /// to ICorpusIndexQuery — the index implementation uses that prefix to
    /// scope SampleAsync/CountAsync to native scraped (doc_) documents only.
    /// </summary>
    [Fact]
    public async Task SamplingPath_PassesSourceWithDocPrefix_ToIndexQuery()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var jjp = RagSourceCatalog.All.Single(s => s.SourceId == "jjp");

        index.CountAsync(jjp, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(jjp, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(jjp, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("doc_abc", "Jersey Jack Pinball",
                 "Rulesheet", "Elton John", "Rules"));
        index.CountAsync(Arg.Is<RagSource>(s => s != jjp), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("tiltforums_elton_john_abc", "Jersey Jack Pinball", "Rulesheet")]);

        await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        // SampleAsync must be called with the unmodified RagSource that has
        // DocumentIdPrefix = "doc_" so the index scopes to native scraped chunks.
        await index.Received(1).SampleAsync(
            Arg.Is<RagSource>(s => s.SourceId == "jjp" && s.DocumentIdPrefix == "doc_"),
            "Rulesheet",
            Arg.Any<CancellationToken>());
    }

    private static CorpusCoverageProber BuildProber(ICorpusIndexQuery index, IRagRetriever retriever) =>
        new(index, retriever, NullLogger<CorpusCoverageProber>.Instance);
}
