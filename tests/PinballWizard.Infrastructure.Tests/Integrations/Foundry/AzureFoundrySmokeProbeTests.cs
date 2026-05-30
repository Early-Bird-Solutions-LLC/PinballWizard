using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Foundry;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Foundry;

// Unit tests for AzureFoundrySmokeProbe's misconfiguration paths. The
// success path requires a deployed Foundry project + AAD; that's
// exercised by the H1 operational hand-off, not unit tests, per the
// DL-0002/DL-0003 lesson — contract tests should hit the real API at
// the live-validation step, not pin a self-defined stub.
public sealed class AzureFoundrySmokeProbeTests
{
    [Fact]
    public async Task ProbeAsync_EmptyEndpoint_ReturnsFailureWithRemediation()
    {
        var options = Options.Create(new AiFoundryOptions
        {
            ProjectEndpoint = string.Empty,
        });
        var probe = new AzureFoundrySmokeProbe(options, NullLogger<AzureFoundrySmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.FoundProjectEndpoint);
        Assert.False(result.ChatDeploymentFound);
        Assert.False(result.EmbeddingDeploymentFound);
        Assert.NotNull(result.Error);
        Assert.Contains(AiFoundryOptions.ProjectEndpointKey, result.Error);
    }

    [Fact]
    public async Task ProbeAsync_WhitespaceEndpoint_ReturnsFailureWithRemediation()
    {
        var options = Options.Create(new AiFoundryOptions
        {
            ProjectEndpoint = "   ",
        });
        var probe = new AzureFoundrySmokeProbe(options, NullLogger<AzureFoundrySmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_MalformedEndpoint_ReturnsFailureWithRemediation()
    {
        var options = Options.Create(new AiFoundryOptions
        {
            ProjectEndpoint = "not a real url",
        });
        var probe = new AzureFoundrySmokeProbe(options, NullLogger<AzureFoundrySmokeProbe>.Instance);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("not a real url", result.FoundProjectEndpoint);
        Assert.False(result.ChatDeploymentFound);
        Assert.False(result.EmbeddingDeploymentFound);
        Assert.NotNull(result.Error);
        Assert.Contains("not a valid absolute URL", result.Error);
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AzureFoundrySmokeProbe(null!, NullLogger<AzureFoundrySmokeProbe>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var options = Options.Create(new AiFoundryOptions { ProjectEndpoint = "https://example.com" });
        Assert.Throws<ArgumentNullException>(() => new AzureFoundrySmokeProbe(options, null!));
    }
}
