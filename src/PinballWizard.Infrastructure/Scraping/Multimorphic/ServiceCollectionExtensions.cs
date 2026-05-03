using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.Multimorphic;

/// <summary>
/// DI registration helpers for the Multimorphic scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Multimorphic scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="MultimorphicOptions"/> from configuration section <c>Multimorphic</c> with validation.</item>
    ///   <item>Registers typed <see cref="HttpClient"/>s for <see cref="MultimorphicSitemapClient"/> and <see cref="MultimorphicProductScraper"/> with the polite User-Agent.</item>
    ///   <item>Bridges the typed-client registration into the <see cref="ISourceScraper"/> enumerable.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddMultimorphicScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<MultimorphicOptions>()
            .Bind(configuration.GetSection(MultimorphicOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<MultimorphicSitemapClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var mm = sp.GetRequiredService<IOptions<MultimorphicOptions>>().Value;
            client.BaseAddress = new Uri(mm.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<MultimorphicProductScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var mm = sp.GetRequiredService<IOptions<MultimorphicOptions>>().Value;
            client.BaseAddress = new Uri(mm.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<MultimorphicProductScraper>());

        return services;
    }
}
