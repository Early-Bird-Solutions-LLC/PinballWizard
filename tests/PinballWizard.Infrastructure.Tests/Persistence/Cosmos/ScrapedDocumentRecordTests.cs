using PinballWizard.Application.Linking;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// Pins the <see cref="EditionScope"/> → wire-string contract for the
/// <c>scraped_documents</c> writer. The wire vocabulary
/// (single-edition / edition-subset / franchise-wide) is consumed downstream
/// by the chunk pipeline, the AI Search filter, and the Wizard prompt, so it
/// must stay stable — and an unmapped scope must FAIL LOUD, never silently
/// fall through to the over-broad "franchise-wide" (the over-citation failure
/// AB#259 exists to prevent).
/// </summary>
public sealed class ScrapedDocumentRecordTests
{
    [Theory]
    [InlineData(EditionScope.SingleEdition, "single-edition")]
    [InlineData(EditionScope.EditionSubset, "edition-subset")]
    [InlineData(EditionScope.FranchiseWide, "franchise-wide")]
    public void ToWire_KnownScope_MapsToHyphenatedWireForm(EditionScope scope, string expected)
    {
        Assert.Equal(expected, ScrapedDocumentRecord.ToWire(scope));
    }

    [Fact]
    public void ToWire_UnmappedScope_Throws_RatherThanSilentlyDefaulting()
    {
        // A value outside the defined enum range stands in for "a new EditionScope
        // value was added without extending ToWire". It must throw, not return a
        // plausible-but-wrong wire string that would pass every other test.
        var unmapped = (EditionScope)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrapedDocumentRecord.ToWire(unmapped));
    }
}
