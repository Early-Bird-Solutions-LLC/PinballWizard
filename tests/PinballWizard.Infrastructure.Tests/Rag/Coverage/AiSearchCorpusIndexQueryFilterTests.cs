using PinballWizard.Application.Rag.Coverage;
using PinballWizard.Infrastructure.Rag.Coverage;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Coverage;

public sealed class AiSearchCorpusIndexQueryFilterTests
{
    [Fact]
    public void SourceFilter_ScrapedManufacturer_CombinesManufacturerAndDocPrefix()
    {
        var stern = new RagSource("stern", ["Stern"], "doc_", true);
        Assert.Equal(
            "(manufacturer eq 'Stern') and (document_id ge 'doc_' and document_id lt 'doc`')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(stern));
    }

    [Fact]
    public void SourceFilter_MultipleManufacturerValues_OrsThem()
    {
        var spooky = new RagSource("spooky", ["Spooky", "Spooky Pinball"], "doc_", true);
        Assert.Equal(
            "(manufacturer eq 'Spooky' or manufacturer eq 'Spooky Pinball') and (document_id ge 'doc_' and document_id lt 'doc`')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(spooky));
    }

    [Fact]
    public void SourceFilter_Kineticist_UsesPrefixOnly()
    {
        var kin = new RagSource("kineticist_tutorials", [], "kineticist_", true);
        Assert.Equal(
            "(document_id ge 'kineticist_' and document_id lt 'kineticist`')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(kin));
    }

    [Fact]
    public void SourceFilter_EscapesApostropheInManufacturer()
    {
        var s = new RagSource("x", ["O'Brien"], "doc_", true);
        Assert.Equal(
            "(manufacturer eq 'O''Brien') and (document_id ge 'doc_' and document_id lt 'doc`')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(s));
    }
}
