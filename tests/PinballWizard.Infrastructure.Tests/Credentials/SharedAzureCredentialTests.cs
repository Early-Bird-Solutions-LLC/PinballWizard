using PinballWizard.Infrastructure.Credentials;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Credentials;

// Behavior under test: the shared DefaultAzureCredential excludes the
// Managed-Identity and workload-identity credentials in Development (a dev
// machine has no IMDS endpoint, so probing it only burns a timeout and emits
// the misleading "All Managed Identity sources are unavailable" error), but
// keeps them in deployed environments where Managed Identity is the ONLY
// available credential. The process-timeout is set in both modes.
public sealed class SharedAzureCredentialTests
{
    [Fact]
    public void BuildOptions_InDevelopment_ExcludesManagedAndWorkloadIdentity()
    {
        var options = SharedAzureCredential.BuildOptions(isDevelopment: true);

        Assert.True(options.ExcludeManagedIdentityCredential);
        Assert.True(options.ExcludeWorkloadIdentityCredential);
    }

    [Fact]
    public void BuildOptions_WhenDeployed_KeepsManagedAndWorkloadIdentity()
    {
        var options = SharedAzureCredential.BuildOptions(isDevelopment: false);

        Assert.False(options.ExcludeManagedIdentityCredential);
        Assert.False(options.ExcludeWorkloadIdentityCredential);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildOptions_AlwaysSetsProcessTimeout(bool isDevelopment)
    {
        // The CLI process-spawn timeout (issue #362 — concurrent az.cmd spawns
        // under eval load) applies in both modes.
        var options = SharedAzureCredential.BuildOptions(isDevelopment);

        Assert.Equal(TimeSpan.FromSeconds(30), options.CredentialProcessTimeout);
    }
}
