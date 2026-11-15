using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Players;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Ifpa;

// DI registration for IFPA WPPR live-player tools (ADR-0067).
//
// Call sites (Api / Web / Cli) gate on key presence:
//   if (!string.IsNullOrWhiteSpace(configuration[IfpaOptions.ApiKeyKey]))
//       services.AddIfpaIntegration(configuration);
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIfpaIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<IfpaOptions>()
            .Bind(configuration.GetSection(IfpaOptions.SectionName))
            .PostConfigure(opts =>
            {
                if (string.IsNullOrWhiteSpace(opts.BaseUrl))
                {
                    opts.BaseUrl = "https://api.ifpapinball.com/";
                }
            })
            .ValidateDataAnnotations()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Ifpa:ApiKey is required.")
            .ValidateOnStart();

        services.AddHttpClient<IfpaClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<IfpaOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            if (sp.GetService<IOptions<PolitenessOptions>>()?.Value?.UserAgent is { Length: > 0 } ua)
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            }
            else
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "PinballWizard/1.0 (+https://pinwiz.ai)");
            }

            client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
        });

        services.AddSingleton<IIfpaClient>(sp => sp.GetRequiredService<IfpaClient>());
        services.AddSingleton<IIfpaPlayerProvider, IfpaPlayerProvider>();

        return services;
    }

    public static bool IsIfpaConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[IfpaOptions.ApiKeyKey]);
    }
}
