using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.PinballMap;

/// <summary>
/// DI registration helpers for the Pinball Map integration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Pinball Map integration:
    /// <list type="bullet">
    ///   <item>Binds <see cref="PinballMapOptions"/> from configuration section <c>PinballMap</c> with validation.</item>
    ///   <item>Registers a typed <see cref="HttpClient"/> for <see cref="PinballMapClient"/> with the polite User-Agent applied.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPinballMapIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<PinballMapOptions>()
            .Bind(configuration.GetSection(PinballMapOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<PinballMapClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var pinballMap = sp.GetRequiredService<IOptions<PinballMapOptions>>().Value;

            client.BaseAddress = new Uri(pinballMap.BaseUrl, UriKind.Absolute);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            // HttpClient.Timeout is the outer wall — the inner timing
            // logic is the resilience handler's TotalRequestTimeout
            // (configured in ServiceDefaults).
            client.Timeout = TimeSpan.FromSeconds(pinballMap.HttpTimeoutSeconds);
        });

        return services;
    }
}
