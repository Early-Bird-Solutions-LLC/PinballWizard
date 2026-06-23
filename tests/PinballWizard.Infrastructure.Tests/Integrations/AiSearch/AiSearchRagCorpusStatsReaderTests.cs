using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.AiSearch;

// Unit tests for AiSearchRagCorpusStatsReader's config-validation paths. Per the
// DL-0002/DL-0003 lesson (mirrored from AzureAiSearchSmokeProbeTests), the
// wire-success path (GetDocumentCountAsync / facet / freshness against a real index)
// is validated at the live operational hand-off + the /admin/corpus axe route, NOT
// pinned with a self-defined SearchClient stub. These tests cover only the early
// returns that never touch the wire.
public sealed class AiSearchRagCorpusStatsReaderTests
{
    private static AiSearchRagCorpusStatsReader Reader(string endpoint) =>
        new(Options.Create(new AiSearchOptions { Endpoint = endpoint }),
            NullLogger<AiSearchRagCorpusStatsReader>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCorpusStatsAsync_BlankEndpoint_ThrowsUnavailableBeforeWire(string endpoint)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader(endpoint).GetCorpusStatsAsync(CancellationToken.None));

        Assert.Contains(AiSearchOptions.EndpointKey, ex.Message);
    }

    [Fact]
    public async Task GetCorpusStatsAsync_MalformedEndpoint_ThrowsUnavailableBeforeWire()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader("not a real url").GetCorpusStatsAsync(CancellationToken.None));

        Assert.Contains(AiSearchOptions.EndpointKey, ex.Message);
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AiSearchRagCorpusStatsReader(null!, NullLogger<AiSearchRagCorpusStatsReader>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AiSearchRagCorpusStatsReader(
                Options.Create(new AiSearchOptions { Endpoint = "https://x.search.windows.net" }), null!));
    }
}
