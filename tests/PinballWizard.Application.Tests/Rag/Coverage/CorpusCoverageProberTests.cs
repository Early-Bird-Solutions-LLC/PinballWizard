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
        // Retrieval returns a DIFFERENT source's chunk (a scraped doc_), not the Kineticist cell.
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("doc_other", "Stern", "Manual")]);

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

    private static CorpusCoverageProber BuildProber(ICorpusIndexQuery index, IRagRetriever retriever) =>
        new(index, retriever, NullLogger<CorpusCoverageProber>.Instance);
}
