using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringStatsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMonitoringStatsRead_RegistersReader()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        services.AddMonitoringStatsRead(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<LogAnalyticsMonitoringStatsReader>(
            provider.GetRequiredService<IMonitoringStatsReader>());
    }
}
