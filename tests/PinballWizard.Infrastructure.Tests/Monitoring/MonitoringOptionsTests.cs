using Microsoft.Extensions.Configuration;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringOptionsTests
{
    [Fact]
    public void Binds_WorkspaceId_And_DefaultsPathPrefix()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:LogAnalyticsWorkspaceId"] = "ws-guid-123",
            })
            .Build();

        var opts = new MonitoringOptions();
        config.GetSection(MonitoringOptions.SectionName).Bind(opts);

        Assert.Equal("ws-guid-123", opts.LogAnalyticsWorkspaceId);
        Assert.Equal("/api/wizard/", opts.WizardApiPathPrefix);
    }
}
