using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    ///   <item>Registers <see cref="ICosmosProvisioner"/> — chooses <see cref="ArmCosmosProvisioner"/> when <see cref="CosmosOptions.AccountResourceId"/> is set (deployed-Cosmos / AAD-auth path) or <see cref="DataPlaneCosmosProvisioner"/> otherwise (Aspire preview emulator / master-key path). Cosmos's data-plane RBAC genuinely does NOT model schema-mutation actions, so the ARM path is required for AAD-authed clients to bootstrap database/container CRUD.</item>
    ///   <item>Registers <see cref="CosmosBootstrapper"/> for ensure-created use on startup. The bootstrapper delegates to the registered <see cref="ICosmosProvisioner"/>.</item>
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
                ApplicationPreferredRegions = [.. options.PreferredRegions],
                Serializer = new SystemTextJsonCosmosSerializer(jsonOptions),
                ConnectionMode = ConnectionMode.Direct,
                ConsistencyLevel = ConsistencyLevel.Session,
            };

            // CosmosClientOptions.ApplicationName is appended to the
            // User-Agent header, and the HTTP-headers parser throws on
            // empty/null values ('Application name "" is invalid' →
            // 'The format of value "<null>" is invalid'). Only assign
            // when the option is populated; CosmosOptions.ApplicationName
            // defaults to null because it is genuinely optional per its
            // docstring (helpful for diagnostics, not load-bearing).
            if (!string.IsNullOrWhiteSpace(options.ApplicationName))
            {
                clientOptions.ApplicationName = options.ApplicationName;
            }

            return new CosmosClient(options.AccountEndpoint, credential, clientOptions);
        });

        // Provisioner selection: ARM for AAD-authed clients (deployed Cosmos),
        // data-plane SDK for master-key-authed clients (Aspire preview emulator).
        // The signal is `Cosmos:AccountResourceId` — set by the deploying
        // operator from the Bicep output `cosmosAccountResourceId` when
        // running against deployed Cosmos; left null when running against
        // the local emulator (which uses the connection string's master key
        // via Aspire's `AddAzureCosmosClient`).
        services.TryAddSingleton<ICosmosProvisioner>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.AccountResourceId))
            {
                // ResourceIdentifier's ctor does not validate the input string —
                // it accepts arbitrary text. The most common operator mistake is
                // pasting the documentEndpoint URL where the ARM resource ID was
                // expected, so guard up-front with a shape check at DI-resolution
                // time and a remediation message that names the right `az` query.
                if (!IsLikelyCosmosAccountResourceId(options.AccountResourceId))
                {
                    throw new InvalidOperationException(
                        $"Cosmos:AccountResourceId is not a well-formed ARM resource identifier: '{options.AccountResourceId}'. " +
                        "Source it from the Bicep output `cosmosAccountResourceId` or via " +
                        "`az cosmosdb show -n <account> -g <rg> --query id -o tsv`. Expected shape: " +
                        "`/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.DocumentDB/databaseAccounts/{name}`.");
                }
                var accountId = new ResourceIdentifier(options.AccountResourceId);
                var credential = sp.GetRequiredService<TokenCredential>();
                var armClient = new ArmClient(credential);
                return new ArmCosmosProvisioner(
                    armClient,
                    accountId,
                    sp.GetRequiredService<ILogger<ArmCosmosProvisioner>>());
            }
            var cosmos = sp.GetRequiredService<CosmosClient>();
            return new DataPlaneCosmosProvisioner(
                cosmos,
                sp.GetRequiredService<ILogger<DataPlaneCosmosProvisioner>>());
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

    /// <summary>
    /// Returns true if the candidate looks like an ARM resource ID for a
    /// Cosmos account: starts with <c>/subscriptions/</c> and contains the
    /// <c>Microsoft.DocumentDB/databaseAccounts/</c> provider segment.
    /// Used as a friendly-error guard in DI registration, not as full
    /// validation — well-formed values still pass through to
    /// <see cref="ResourceIdentifier"/> which does the rigorous parsing.
    /// </summary>
    private static bool IsLikelyCosmosAccountResourceId(string candidate) =>
        candidate.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
        && candidate.Contains("/Microsoft.DocumentDB/databaseAccounts/", StringComparison.OrdinalIgnoreCase);
}
