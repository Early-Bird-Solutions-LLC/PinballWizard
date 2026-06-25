using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.WooCommerce;

namespace PinballWizard.Infrastructure.Scraping.BarrelsOfFun;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBarrelsOfFunScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<BarrelsOfFunOptions>()
            .Bind(configuration.GetSection(BarrelsOfFunOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<WooCommerceStoreApiClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<BofProductScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var bof = sp.GetRequiredService<IOptions<BarrelsOfFunOptions>>().Value;
            client.BaseAddress = new Uri(bof.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<BofProductScraper>());

        return services;
    }
}
