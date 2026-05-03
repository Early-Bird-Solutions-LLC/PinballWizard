using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// DI registration helpers for the Cosmos persistence layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Cosmos persistence layer:
    /// <list type="bullet">
    ///   <item>Binds <see cref="CosmosOptions"/> from configuration section <c>Cosmos</c> with validation.</item>
    ///   <item>Registers <see cref="CosmosClient"/> as a singleton via <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/>: when an external integration (e.g., .NET Aspire's <c>AddAzureCosmosClient("cosmos")</c>) has already registered a <see cref="CosmosClient"/>, that registration is preserved and this fallback is skipped. Otherwise a Managed-Identity-authenticated client is constructed from <see cref="CosmosOptions.AccountEndpoint"/>.</item>
    ///   <item>Registers per-entity <see cref="Container"/> wrappers as keyed services.</item>
    ///   <item>Registers <see cref="IMachineRepository"/> and <see cref="IIngestionSourceRepository"/>.</item>
    ///   <item>Registers <see cref="CosmosBootstrapper"/> for ensure-created use on startup.</item>
    /// </list>
    /// Local-auth / connection-string paths are intentionally not supported via the fallback — production deploys use Managed Identity per ADR 0009 spirit (no shared secrets in container env vars). The Aspire path covers local-emulator connection-string flow for development.
    /// </summary>
    public static IServiceCollection AddCosmosPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<CosmosOptions>()
            .Bind(configuration.GetSection(CosmosOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.AccountEndpoint))
            {
                throw new InvalidOperationException(
                    "Cosmos:AccountEndpoint is not configured and no CosmosClient has been registered by an external integration. " +
                    "Either set Cosmos:AccountEndpoint in configuration (Managed-Identity path), or register a CosmosClient via " +
                    ".NET Aspire's AddAzureCosmosClient(\"cosmos\") before calling AddCosmosPersistence.");
            }
            var credential = sp.GetRequiredService<TokenCredential>();

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            var clientOptions = new CosmosClientOptions
            {
                ApplicationName = options.ApplicationName,
                ApplicationPreferredRegions = [.. options.PreferredRegions],
                Serializer = new SystemTextJsonCosmosSerializer(jsonOptions),
                ConnectionMode = ConnectionMode.Direct,
                ConsistencyLevel = ConsistencyLevel.Session,
            };

            return new CosmosClient(options.AccountEndpoint, credential, clientOptions);
        });

        services.TryAddSingleton<CosmosBootstrapper>();

        services.AddSingleton<IMachineRepository>(sp =>
        {
            var container = ResolveContainer(sp, "machines");
            return new MachineRepository(container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MachineRepository>>());
        });

        services.AddSingleton<IIngestionSourceRepository>(sp =>
        {
            var container = ResolveContainer(sp, "ingestion_sources");
            return new IngestionSourceRepository(container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<IngestionSourceRepository>>());
        });

        return services;
    }

    private static Container ResolveContainer(IServiceProvider sp, string containerName)
    {
        var client = sp.GetRequiredService<CosmosClient>();
        var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
        return client.GetContainer(options.DatabaseName, containerName);
    }
}
