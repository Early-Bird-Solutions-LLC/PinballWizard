using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// DI registration helpers for the Pinball Brothers scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Pinball Brothers scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="PinballBrothersOptions"/> from configuration section <c>PinballBrothers</c> with validation.</item>
    ///   <item>Registers a typed <see cref="HttpClient"/> for <see cref="PbWpPagesClient"/> that asks for JSON.</item>
    ///   <item>Registers <see cref="PbGamePageScraper"/> as both itself and an <see cref="ISourceScraper"/>.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPinballBrothersScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<PinballBrothersOptions>()
            .Bind(configuration.GetSection(PinballBrothersOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<PbWpPagesClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var pb = sp.GetRequiredService<IOptions<PinballBrothersOptions>>().Value;
            client.BaseAddress = new Uri(pb.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<PbGamePageScraper>();
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PbGamePageScraper>());

        // Document scraper: discovers per-game PDF rulesheets / manuals from
        // the content.rendered field of each game page via WP REST. Reuses
        // the same PbWpPagesClient and PinballBrothersOptions as the game-page
        // scraper; the two scrapers share one HttpClient registration.
        services.AddTransient<PbGamePageDocumentScraper>();
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PbGamePageDocumentScraper>());

        return services;
    }
}
