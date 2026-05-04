using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// DI registration helpers for the OPDB integration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OPDB integration:
    /// <list type="bullet">
    ///   <item>Binds <see cref="OpdbOptions"/> from configuration section <c>Opdb</c> with validation.</item>
    ///   <item>Registers a typed <see cref="HttpClient"/> for <see cref="OpdbClient"/> with the polite User-Agent applied.</item>
    ///   <item>Registers <see cref="IOpdbSyncService"/> as a transient (one sync run per request).</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddOpdbIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<OpdbOptions>()
            .Bind(configuration.GetSection(OpdbOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<OpdbClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var opdb = sp.GetRequiredService<IOptions<OpdbOptions>>().Value;

            client.BaseAddress = new Uri(opdb.BaseUrl, UriKind.Absolute);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            // HttpClient.Timeout is the outer wall — the inner timing
            // logic is the resilience handler's TotalRequestTimeout
            // (configured in ServiceDefaults). Both must be generous
            // enough for OPDB's `/api/export` bulk response.
            client.Timeout = TimeSpan.FromSeconds(opdb.HttpTimeoutSeconds);
        });

        services.AddTransient<IOpdbSyncService, OpdbSyncService>();

        return services;
    }
}
