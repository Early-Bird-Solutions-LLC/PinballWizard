using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Kineticist;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Kineticist;

// DI registration for the Kineticist keyed API (ADR-0043 Tier A/B).
//
// Call site (Cli / Api / Web) gates this on ApiKeyKey presence:
//   if (!string.IsNullOrWhiteSpace(configuration[KineticistOptions.ApiKeyKey]))
//       services.AddKineticistIntegration(configuration);
// When absent, IKineticistGuideLinkProvider is not registered and
// KineticistGuideLinkTool returns null (fail closed — no scrape fallback).
//
// The tutorials scraper (AddKineticistScraping) is a separate registration
// used only by the CLI sync verb; this integration is the live Wizard path.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKineticistIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<KineticistOptions>()
            .Bind(configuration.GetSection(KineticistOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<KineticistApiClient>((sp, client) =>
        {
            // Partner API — not a PoliteScraperBase path. Courtesy User-Agent
            // when PolitenessOptions is wired; otherwise a stable default.
            if (sp.GetService<IOptions<PolitenessOptions>>()?.Value?.UserAgent is { Length: > 0 } ua)
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            }
            else
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "PinballWizard/0.1 (+https://github.com/Early-Bird-Solutions-LLC/PinballWizard)");
            }

            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Prefer the typed HttpClient registration; TryAdd so a host that
        // also called AddKineticistScraping does not double-bind the interface.
        services.TryAddTransient<IKineticistApiClient>(sp =>
            sp.GetRequiredService<KineticistApiClient>());

        services.TryAddSingleton<IKineticistGuideLinkProvider, KineticistGuideLinkProvider>();

        return services;
    }
}
