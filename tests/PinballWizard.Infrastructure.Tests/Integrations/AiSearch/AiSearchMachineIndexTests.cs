using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.AiSearch;

// Unit tests for AiSearchMachineIndex.BuildSearchOptions — pins the OData filter
// generation logic without requiring a live AI Search client. Mirrors the
// internal-static pinning pattern used by AiSearchRagCorpusStatsReaderTests.
public sealed class AiSearchMachineIndexTests
{
    [Fact]
    public void BuildSearchOptions_WithManufacturerKey_EmitsPartitionFilter()
    {
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: "stern");
        Assert.Equal("manufacturer_key eq 'stern'", options.Filter);
        Assert.Equal(5, options.Size);
    }

    [Fact]
    public void BuildSearchOptions_NullManufacturerKey_NoFilter()
    {
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: null);
        Assert.Null(options.Filter);
    }

    [Fact]
    public void BuildSearchOptions_ManufacturerKeyWithApostrophe_IsOdataEscaped()
    {
        // OData escapes a single quote by doubling it. Defensive — real keys are
        // lowercase alnum/underscore, but the query builder must never emit a
        // malformed / injectable filter.
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: "o'brien");
        Assert.Equal("manufacturer_key eq 'o''brien'", options.Filter);
    }
}
