using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Integrations.AiSearch;

// Unit tests for AzureAiSearchSmokeProbe's misconfiguration paths. The
// success path requires a deployed AI Search service + AAD; that's
// exercised by the H1 operational hand-off, not unit tests, per the
// DL-0002/DL-0003 lesson — contract tests should hit the real API at
// the live-validation step, not pin a self-defined stub. Mirrors the
// AzureFoundrySmokeProbeTests shape.
public sealed class AzureAiSearchSmokeProbeTests
{
    [Fact]
    public async Task ProbeAsync_EmptyEndpoint_ReturnsFailureWithRemediation()
    {
        var options = Options.Create(new AiSearchOptions
        {
            Endpoint = string.Empty,
        });
        var probe = new AzureAiSearchSmokeProbe(options, NullLogger<AzureAiSearchSmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.FoundEndpoint);
        Assert.Equal("pinwiz-rag-v1", result.ExpectedIndexName);
        Assert.NotNull(result.Error);
        Assert.Contains(AiSearchOptions.EndpointKey, result.Error);
    }

    [Fact]
    public async Task ProbeAsync_WhitespaceEndpoint_ReturnsFailureWithRemediation()
    {
        var options = Options.Create(new AiSearchOptions
        {
            Endpoint = "   ",
        });
        var probe = new AzureAiSearchSmokeProbe(options, NullLogger<AzureAiSearchSmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_MalformedEndpoint_ReturnsFailureWithRemediation()
    {
        var options = Options.Create(new AiSearchOptions
        {
            Endpoint = "not a real url",
        });
        var probe = new AzureAiSearchSmokeProbe(options, NullLogger<AzureAiSearchSmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("not a real url", result.FoundEndpoint);
        Assert.NotNull(result.Error);
        Assert.Contains("not a valid absolute URL", result.Error);
        Assert.Contains(AiSearchOptions.EndpointKey, result.Error);
    }

    [Fact]
    public async Task ProbeAsync_PreservesCustomIndexNameInResult()
    {
        var options = Options.Create(new AiSearchOptions
        {
            Endpoint = string.Empty,
            IndexName = "pinwiz-rag-v2",
        });
        var probe = new AzureAiSearchSmokeProbe(options, NullLogger<AzureAiSearchSmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        // ExpectedIndexName comes through even on failure paths so the H1
        // hand-off can surface "the operator's configuration says vN"
        // alongside the error message.
        Assert.False(result.Success);
        Assert.Equal("pinwiz-rag-v2", result.ExpectedIndexName);
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AzureAiSearchSmokeProbe(null!, NullLogger<AzureAiSearchSmokeProbe>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var options = Options.Create(new AiSearchOptions { Endpoint = "https://example.search.windows.net" });
        Assert.Throws<ArgumentNullException>(() => new AzureAiSearchSmokeProbe(options, null!));
    }
}
