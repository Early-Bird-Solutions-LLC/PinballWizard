using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// DI registration helpers for the Spooky Pinball scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Spooky scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="SpookyOptions"/> from configuration section <c>Spooky</c> with validation.</item>
    ///   <item>Registers a typed <see cref="HttpClient"/> for <see cref="SpookyWpPagesClient"/> that asks for JSON.</item>
    ///   <item>Registers <see cref="SpookyGamePageScraper"/> as both itself and an <see cref="ISourceScraper"/>.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddSpookyPinballScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SpookyOptions>()
            .Bind(configuration.GetSection(SpookyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<SpookyWpPagesClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var spooky = sp.GetRequiredService<IOptions<SpookyOptions>>().Value;
            client.BaseAddress = new Uri(spooky.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<SpookyGamePageScraper>();
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<SpookyGamePageScraper>());

        // Support-page scraper: discovers per-game rule/manual/chart PDFs from
        // the Game Support hub's child pages via WP REST. Uses the same
        // politeness options and SpookyOptions as the game-page scraper.
        services.AddHttpClient<SpookySupportPageScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var spooky = sp.GetRequiredService<IOptions<SpookyOptions>>().Value;
            client.BaseAddress = new Uri(spooky.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<SpookySupportPageScraper>();
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<SpookySupportPageScraper>());

        return services;
    }
}
