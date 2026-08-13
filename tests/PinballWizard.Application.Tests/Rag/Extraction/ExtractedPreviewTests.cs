using PinballWizard.Application.Rag.Extraction;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Extraction;

public sealed class ExtractedPreviewTests
{
    [Fact]
    public void Failure_ProducesEmptyPagesAndCarriesError()
    {
        var result = ExtractedPreview.Failure(ExtractionStatus.Malformed, "boom");

        Assert.Equal(ExtractionStatus.Malformed, result.Status);
        Assert.Empty(result.Pages);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public void DefaultMaxStreamBytes_MatchesOptionsPropertyDefault()
    {
        // Guards the single-source-of-threshold constraint: the const the
        // DocumentLinker ctor defaults to must be the same value the options
        // property defaults to. If someone edits one and not the other, this fails.
        Assert.Equal(PdfExtractionOptions.DefaultMaxStreamBytes, new PdfExtractionOptions().MaxStreamBytes);
    }
}
