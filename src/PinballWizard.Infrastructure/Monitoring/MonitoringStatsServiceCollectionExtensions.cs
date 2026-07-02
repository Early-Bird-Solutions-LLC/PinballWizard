using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

public static class MonitoringStatsServiceCollectionExtensions
{
    public static IServiceCollection AddMonitoringStatsRead(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName));
        // Degrade-at-read: no ValidateOnStart so the Web host starts cleanly
        // with the telemetry source unconfigured (e.g. local dev).
        services.TryAddSingleton(TimeProvider.System);
        // IMemoryCache: AddMemoryCache is idempotent — calling it more than once
        // is harmless. Required by LogAnalyticsMonitoringStatsReader for per-window
        // stampede-safe TTL caching (default 30 s, see MonitoringOptions.CacheTtl).
        services.AddMemoryCache();
        services.TryAddSingleton<IMonitoringStatsReader, LogAnalyticsMonitoringStatsReader>();
        return services;
    }
}
