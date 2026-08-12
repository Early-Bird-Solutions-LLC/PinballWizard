using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Downloading;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Application.Landing;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Ai.Hosting;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Landing;
using PinballWizard.Infrastructure.Resolution;

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

        services.TryAddSingleton<TokenCredential>(_ => Credentials.SharedAzureCredential.Instance);

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

            // Connection mode strategy (per Neighborli reference pattern + ADR-0025):
            // - Gateway + LimitToEndpoint in Development: dev machine → Azure Cosmos
            //   over HTTPS. Direct TCP to partition replicas is unreachable from outside
            //   Azure — Change Feed silently fails to deliver batches in Direct mode.
            // - Direct + PreferredRegions in Production: ACA worker is co-located with
            //   Cosmos so direct TCP works and saves 10–30ms vs Gateway.
            // LimitToEndpoint and ApplicationPreferredRegions are mutually exclusive in
            // the SDK, so the two paths are fully separated on the environment signal.
            var hostEnv = sp.GetRequiredService<IHostEnvironment>();
            var isDevelopment = hostEnv.IsDevelopment();

            var clientOptions = new CosmosClientOptions
            {
                ConnectionMode = isDevelopment ? ConnectionMode.Gateway : ConnectionMode.Direct,
            };
            // Serializer + write-behavior (ADR-0025 § 2) shared with the
            // Aspire-registered client via CosmosClientConfiguration, so the
            // Managed-Identity fallback and the emulator/Aspire path serialize
            // documents identically (same [JsonPropertyName] handling). The
            // divergence here — custom serializer on the fallback only — is what
            // made local-emulator writes fail with 400 before this was shared.
            CosmosClientConfiguration.ApplySharedOptions(clientOptions);

            if (isDevelopment)
            {
                clientOptions.LimitToEndpoint = true;
            }
            else if (options.PreferredRegions.Count > 0)
            {
                clientOptions.ApplicationPreferredRegions = [.. options.PreferredRegions];
            }

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

        services.AddSingleton<IScrapedDocumentRepository>(sp =>
        {
            var container = ResolveContainer(sp, "scraped_documents");
            return new CosmosScrapedDocumentRepository(container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosScrapedDocumentRepository>>());
        });

        // Title→OPDB-ID lookup for the user-delight critical path per
        // ADR-0025 § 4. Inherits metering from `CosmosRepository<T>` so
        // every point-read here lands on `pinwiz.cosmos.*` tagged
        // `container=machine_title_lookups`.
        services.AddSingleton<IMachineTitleLookupRepository>(sp =>
        {
            var container = ResolveContainer(sp, "machine_title_lookups");
            return new MachineTitleLookupRepository(container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MachineTitleLookupRepository>>());
        });

        services.AddSingleton<IRawDocumentRepository>(sp =>
        {
            var container = ResolveContainer(sp, "scraped_documents_raw");
            return new CosmosRawDocumentRepository(container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosRawDocumentRepository>>());
        });

        services.AddSingleton<ILinkOverrideRepository>(sp =>
        {
            var container = ResolveContainer(sp, "link_overrides");
            return new CosmosLinkOverrideRepository(container,
                sp.GetRequiredService<ILogger<CosmosLinkOverrideRepository>>());
        });

        services.AddSingleton<IScrapeRunRepository>(sp =>
        {
            var container = ResolveContainer(sp, "scrape_runs");
            return new CosmosScrapeRunRepository(container,
                sp.GetRequiredService<ILogger<CosmosScrapeRunRepository>>());
        });

        // Runtime-mutable Wizard settings (admin settings plan, PR-B1).
        // Singleton on purpose: the repository's TTL cache is the per-ask
        // read-amortization layer and must be process-wide.
        services.AddSingleton<IAdminSettingsRepository>(sp =>
        {
            var container = ResolveContainer(sp, "admin_settings");
            return new CosmosAdminSettingsRepository(container,
                sp.GetRequiredService<ILogger<CosmosAdminSettingsRepository>>());
        });

        // Per-agent prompt override store (admin prompts plan, PR-B3).
        // Singleton: the repository's TTL cache is process-wide for the
        // same reason as admin_settings above — OverridingAgentPromptProvider
        // calls GetActiveAsync on every ask.
        services.AddSingleton<IAgentPromptOverrideRepository>(sp =>
        {
            var container = ResolveContainer(sp, "admin_prompts");
            return new CosmosAgentPromptOverrideRepository(container,
                sp.GetRequiredService<ILogger<CosmosAgentPromptOverrideRepository>>());
        });

        // Registered HERE (not in the Application Ai extensions) on
        // purpose: IRuntimeSettings requires the repository above, which
        // only exists on Cosmos-wired hosts. Hosts without Cosmos resolve
        // AiRouter with its optional IRuntimeSettings? param defaulted to
        // null and run on IOptions defaults — identical behavior to no
        // stored overrides.
        services.AddSingleton<IRuntimeSettings, RuntimeSettings>();

        // Curated landing-page featured machines per ADR-0026 § Landing surface.
        // Inherits metering from `CosmosRepository<T>` so every SDK call here
        // lands on `pinwiz.cosmos.*` tagged `container=featured_machines`.
        services.AddSingleton<IFeaturedMachineRepository>(sp =>
        {
            var container = ResolveContainer(sp, "featured_machines");
            return new FeaturedMachineRepository(container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FeaturedMachineRepository>>());
        });

        // ADR-0054 resolution core. The loader fails closed on an alias that does not
        // resolve, so it must be able to see the catalog — hence the catalog binding.
        services.AddSingleton<IMachineAliasCatalog>(sp =>
            new CosmosMachineAliasCatalog(sp.GetRequiredService<IMachineRepository>()));

        services.AddSingleton<IMachineAliasLoader>(sp =>
            new MachineAliasLoader(
                sp.GetRequiredService<IMachineAliasCatalog>(),
                sp.GetRequiredService<ILogger<MachineAliasLoader>>()));

        services.AddSingleton<IDocumentLinker>(sp =>
        {
            var rawRepo = sp.GetRequiredService<IRawDocumentRepository>();
            var overrideRepo = sp.GetRequiredService<ILinkOverrideRepository>();
            var machineRepo = sp.GetRequiredService<IMachineRepository>();
            var linkedRepo = sp.GetRequiredService<IScrapedDocumentRepository>();
            var previewExtractor = sp.GetService<IDocumentPreviewExtractor>();
            // Primitive pluck, mirroring cosmosWriteConcurrency below: the
            // options type stays the single source of the threshold without the
            // orchestrator taking a dependency on extraction configuration.
            var pdfOptions = sp.GetService<IOptions<PdfExtractionOptions>>();
            var maxExtractionBytes = pdfOptions?.Value.MaxStreamBytes ?? PdfExtractionOptions.DefaultMaxStreamBytes;
            var logger = sp.GetRequiredService<ILogger<DocumentLinker>>();
            var settings = sp.GetService<IOptions<ScraperSettings>>();
            var concurrency = settings?.Value.CosmosWriteConcurrency ?? 20;
            var blobStore = sp.GetService<IDocumentBlobStore>();
            // ADR-0054: the alias loader turns on the resolver index inside the linker.
            // GetRequiredService — it is registered unconditionally above, and silently
            // running without the resolver would hide a DI regression (invariant #17).
            var aliasLoader = sp.GetRequiredService<IMachineAliasLoader>();
            return new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo, previewExtractor, logger,
                aliasLoader, cosmosWriteConcurrency: concurrency, blobStore: blobStore, maxExtractionBytes: maxExtractionBytes);
        });

        // Document downloader (--download-documents) — fetches not-yet-downloaded
        // raw documents and writes them to the durable pinwiz-raw blob store so
        // content survives across ACA runs (ephemeral /tmp). Reuses the registered
        // IFileDownloader (polite, resilient) and IDocumentBlobStore (managed-identity).
        services.AddSingleton<DocumentDownloadService>(sp =>
        {
            var rawRepo = sp.GetRequiredService<IRawDocumentRepository>();
            var downloader = sp.GetRequiredService<IFileDownloader>();
            var blobStore = sp.GetRequiredService<IDocumentBlobStore>();
            var settings = sp.GetRequiredService<IOptions<ScraperSettings>>();
            var logger = sp.GetRequiredService<ILogger<DocumentDownloadService>>();
            return new DocumentDownloadService(rawRepo, downloader, blobStore, settings, logger);
        });

        // Download-path migration (--migrate-download-paths) — one-shot byte-safe
        // correction of legacy already-rooted file.local_path values. Uses the
        // filesystem store (SHA-256 + move) and the same DownloadsPath root the
        // linker reads from, so the on-disk paths it computes match the linker's.
        services.AddSingleton<IDownloadFileStore, FileSystemDownloadFileStore>();
        services.AddSingleton<DownloadPathMigrationService>(sp =>
        {
            var rawRepo = sp.GetRequiredService<IRawDocumentRepository>();
            var store = sp.GetRequiredService<IDownloadFileStore>();
            var logger = sp.GetRequiredService<ILogger<DownloadPathMigrationService>>();
            var settings = sp.GetService<IOptions<ScraperSettings>>();
            var downloadsRoot = settings?.Value.DownloadsPath
                ?? Path.Combine(AppContext.BaseDirectory, "downloads");
            return new DownloadPathMigrationService(rawRepo, store, logger, downloadsRoot);
        });

        // Per ADR-0025 § 8 — warmup amortizes the SDK's lazy-connection
        // cost off the first user query. Failure is `Warning` not throw;
        // the health check below is the canonical reachability signal.
        services.AddHostedService<CosmosClientWarmupHostedService>();

        // Per ADR-0025 § 8 — Cosmos reachability surfaces via /healthz
        // (tagged `live` so it's part of the liveness probe ACA hits).
        services.AddHealthChecks()
            .AddCheck<CosmosHealthCheck>("cosmos", tags: ["live"]);

        // ICosmosCanaryProbe registered here (not in AddSystemStatusProvider)
        // because CosmosCanaryProbe requires CosmosClient, which is guaranteed
        // available at this point. Registering it in AddSystemStatusProvider
        // would fail at DI-resolution time when Cosmos is absent. The probe is
        // an optional dependency in SystemStatusProvider — absent when Cosmos
        // is not configured, causing SystemStatus.CosmosHealthy = null.
        services.TryAddSingleton<ICosmosCanaryProbe, CosmosCanaryProbe>();

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
