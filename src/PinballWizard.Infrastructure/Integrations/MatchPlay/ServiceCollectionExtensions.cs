using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Tournaments;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// DI registration for Match Play Events live-tournament tools (ADR-0061).
//
// Call sites (Api / Web / Cli) gate on token presence:
//   var token = configuration[MatchPlayOptions.ApiTokenKey]
//            ?? configuration[OpdbOptions.SectionName + ":ApiToken"];
//   if (!string.IsNullOrWhiteSpace(token))
//       services.AddMatchPlayIntegration(configuration);
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMatchPlayIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Prefer MatchPlay:ApiToken; fall back to Opdb:ApiToken (same Match Play bearer).
        var token = configuration[MatchPlayOptions.ApiTokenKey];
        if (string.IsNullOrWhiteSpace(token))
        {
            token = configuration[$"{OpdbOptions.SectionName}:ApiToken"];
        }

        services
            .AddOptions<MatchPlayOptions>()
            .Bind(configuration.GetSection(MatchPlayOptions.SectionName))
            .PostConfigure(opts =>
            {
                if (string.IsNullOrWhiteSpace(opts.ApiToken) && !string.IsNullOrWhiteSpace(token))
                {
                    opts.ApiToken = token!;
                }

                if (string.IsNullOrWhiteSpace(opts.BaseUrl))
                {
                    opts.BaseUrl = "https://api.matchplay.events/";
                }
            })
            .ValidateDataAnnotations()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiToken), "MatchPlay:ApiToken (or Opdb:ApiToken fallback) is required.")
            .ValidateOnStart();

        services.AddHttpClient<MatchPlayClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MatchPlayOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiToken);
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

        services.AddSingleton<IMatchPlayClient>(sp => sp.GetRequiredService<MatchPlayClient>());
        services.AddSingleton<IMatchPlayTournamentProvider, MatchPlayTournamentProvider>();

        return services;
    }

    // Shared gate helper so Api / Web / Cli do not drift on the fallback key.
    public static bool IsMatchPlayConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[MatchPlayOptions.ApiTokenKey])
            || !string.IsNullOrWhiteSpace(configuration[$"{OpdbOptions.SectionName}:ApiToken"]);
    }
}
