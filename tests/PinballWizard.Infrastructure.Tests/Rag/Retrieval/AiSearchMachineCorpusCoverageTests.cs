using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Retrieval;

public sealed class AiSearchMachineCorpusCoverageTests
{
    // Safety invariant (ADR-0053): the coverage count filter MUST be
    // byte-identical to the retriever's machine-scoped filter, so a
    // "zero content" verdict can never disagree with what the agent's
    // own machine-scoped search would see. Both derive from BuildFilter.
    [Fact]
    public void CountFilter_IsIdenticalTo_RetrieverMachineFilter()
    {
        const string machineId = "GRBN-MQR4P";

        var coverageFilter = AiSearchMachineCorpusCoverage.BuildCountFilter(machineId);
        var retrieverFilter = AiSearchRagRetriever.BuildFilter(
            new RetrievalOptions(MachineId: machineId));

        // Tautological today (both from BuildFilter) but becomes a real cross-check if BuildCountFilter ever gets an independent implementation.
        Assert.Equal(retrieverFilter, coverageFilter);
        Assert.Equal("machine_id eq 'GRBN-MQR4P'", coverageFilter);
    }

    // OData escaping must also be identical for ids containing an
    // apostrophe (a fan-named machine), or the two filters could diverge
    // on exactly the untrusted-input case escaping exists to handle.
    [Fact]
    public void CountFilter_EscapesApostrophe_IdenticalToRetriever()
    {
        const string machineId = "O'Brien-1";

        var coverageFilter = AiSearchMachineCorpusCoverage.BuildCountFilter(machineId);
        var retrieverFilter = AiSearchRagRetriever.BuildFilter(
            new RetrievalOptions(MachineId: machineId));

        // Tautological today (both from BuildFilter) but becomes a real cross-check if BuildCountFilter ever gets an independent implementation.
        Assert.Equal(retrieverFilter, coverageFilter);
        Assert.Equal("machine_id eq 'O''Brien-1'", coverageFilter);
    }
}
