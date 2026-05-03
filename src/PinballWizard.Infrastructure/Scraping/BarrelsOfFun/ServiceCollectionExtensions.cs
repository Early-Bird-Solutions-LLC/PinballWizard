using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.BarrelsOfFun;

/// <summary>
/// DI registration helpers for the Barrels of Fun scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Barrels of Fun scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="BarrelsOfFunOptions"/> from configuration section <c>BarrelsOfFun</c> with validation.</item>
    ///   <item>Registers typed <see cref="HttpClient"/>s for <see cref="BofCategoryClient"/> and <see cref="BofProductScraper"/> with the polite User-Agent.</item>
    ///   <item>Bridges the typed-client registration into the <see cref="ISourceScraper"/> enumerable.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddBarrelsOfFunScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<BarrelsOfFunOptions>()
            .Bind(configuration.GetSection(BarrelsOfFunOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<BofCategoryClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var bof = sp.GetRequiredService<IOptions<BarrelsOfFunOptions>>().Value;
            client.BaseAddress = new Uri(bof.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<BofProductScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var bof = sp.GetRequiredService<IOptions<BarrelsOfFunOptions>>().Value;
            client.BaseAddress = new Uri(bof.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<BofProductScraper>());

        return services;
    }
}
