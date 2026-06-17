// Aspire AppHost for PinballWizard.
//
// Today this orchestrates only the local Cosmos preview emulator. As Phase 2
// services land (Track D RAG processor, Blazor Web, Admin behind /admin) they
// are added here with `.WithReference(cosmosDb).WaitFor(cosmosDb)` so they
// pick up the connection string and start in dependency order.
//
// Local dev runs the preview emulator (no Azure auth required). When
// the AppHost is published as a Bicep manifest for cloud deploy, Aspire
// substitutes the real Cosmos account in its place.
//
// Cosmos preview emulator caveats per Neighborli's working setup:
//  - It is `[Experimental]` so the `ASPIRECOSMOSDB001` analyzer warning
//    must be suppressed pragma-wise. Wide-suppression in csproj would
//    hide future emulator API churn; keep the suppression scoped.
//  - It does not currently support `WaitFor` / `WithReference` against
//    arbitrary downstream resources — dependency ordering must be
//    handled at the application layer (CosmosBootstrapper.EnsureCreatedAsync
//    is the project's existing answer).
//  - It bundles its own PostgreSQL backend; do NOT inject external
//    PostgreSQL env vars or you will overwrite the auto-generated
//    credentials and the connection will fail on first read.
//  - First run pulls ~3 GB of container images (the Cosmos emulator
//    plus its bundled PostgreSQL); subsequent runs reuse the persistent
//    data volume — meaning seeded data also persists across restarts.
//    To reset, run `docker volume rm` against the Aspire-named volume.
//
// PinballWizard.Cli is project-referenced from the csproj so Aspire's
// source generator emits Projects.PinballWizard_Cli for future use. The
// CLI is not added to orchestration today (it is a one-shot scraper run,
// not a long-running service); the follow-up PR wires it once
// AddCosmosPersistence is hooked up in Program.cs and the Aspire-injected
// connection string is the canonical source.

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIRECOSMOSDB001 // Cosmos preview emulator is experimental — see file-level remarks for why we accept it.
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsPreviewEmulator(emulator =>
    {
        emulator.WithLifetime(ContainerLifetime.Persistent);
        emulator.WithDataVolume();
        emulator.WithDataExplorer();
        emulator.WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "true");
    });
#pragma warning restore ASPIRECOSMOSDB001

// Single shared database — `pinwiz`. Container creation is deferred to
// the application layer (CosmosBootstrapper.EnsureCreatedAsync) so the
// container schema is owned where it is consumed, and so the runtime
// path that creates containers in Azure (where Bicep doesn't yet declare
// them) is the same path exercised in local dev.
_ = cosmos.AddCosmosDatabase("pinwiz");

// Azure Storage via Azurite (the official Microsoft Storage emulator).
// Local-dev replacement for the deployed Storage account that the Phase 2
// Bicep gating defers (raw / processed / photos blob containers from
// docs/infra_analysis.md §1). RunAsEmulator() launches the Azurite
// container; the persistent data volume mirrors the Cosmos pattern so
// seeded blobs survive AppHost restarts.
//
// No Phase 1 code consumes this connection yet — it is wired so future
// services (Track D RAG ingestion writes raw blobs; Blazor reads photos)
// pick it up without an AppHost change at the moment they need it.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator =>
    {
        emulator.WithLifetime(ContainerLifetime.Persistent);
        emulator.WithDataVolume();
    });
_ = storage.AddBlobs("blobs");

// PinballWizard.Api — JSON / SSE host per ADR-0026 § 1.
// Wired with a Cosmos reference so Wave 2 Api endpoints that query
// featured_machines / ingestion_sources work without an AppHost change.
// See ADR-0026 § 1 for the rationale behind a separate Api project.
//
// NOTE: WaitFor not chained — same reasoning as the Web project below.
var api = builder.AddProject<Projects.PinballWizard_Api>("pinwiz-api")
    .WithReference(cosmos)
    // Declare endpoints explicitly. A project with no launchSettings.json gets
    // NO HTTP/HTTPS bindings from Aspire by default, so Kestrel falls back to
    // its hardcoded http://localhost:5000 — and pinwiz-api + pinwiz-web would
    // both land on 5000 and collide. Declaring endpoints here (no fixed port =
    // Aspire assigns a free port per run and injects ASPNETCORE_URLS) is the
    // 13.x-preferred source of truth, and gives WithReference(api) below a real
    // endpoint to publish for service discovery ("https+http://pinwiz-api").
    // Ref: https://aspire.dev/integrations/dotnet/project-resources/
    .WithHttpEndpoint()
    .WithHttpsEndpoint()
    // Run the child in the AppHost's environment (Development locally). With no
    // launchSettings.json, Aspire sets no ASPNETCORE_ENVIRONMENT, so the project
    // defaults to Production — and in Production MapStaticAssets() expects the
    // published wwwroot, which doesn't exist when debugging from bin/Debug, so
    // every _content/* RCL asset, the scoped *.styles.css bundle, and
    // _framework/blazor.web.js 500 with FileNotFound. Development auto-enables
    // the static-web-assets loader. Propagated (not hardcoded) so a published
    // AppHost would still hand children Production.
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName);

// Blazor Web App — Wave 1 PR-F0 (amended by PR-F2). Wired with a Cosmos
// reference and the Api reference so Aspire injects the service-discovery
// env vars (services__pinwiz-api__http__0) that WizardStreamingClient
// resolves via "https+http://pinwiz-api". See ADR-0026 § 1/2.
//
// NOTE: The Cosmos preview emulator comment at the top of this file
// documents why WaitFor is not chained here — dependency ordering is
// handled at the application layer (CosmosBootstrapper.EnsureCreatedAsync).
var web = builder.AddProject<Projects.PinballWizard_Web>("pinwiz-web")
    .WithReference(cosmos)
    .WithReference(api)
    // See the pinwiz-api endpoint note above — declared explicitly so the Blazor
    // host binds an Aspire-assigned port instead of the default 5000.
    .WithHttpEndpoint()
    .WithHttpsEndpoint()
    // See the pinwiz-api ASPNETCORE_ENVIRONMENT note — required here too, and
    // load-bearing for the Blazor host: Production breaks MudBlazor / _framework
    // static assets (MapStaticAssets) when debugging from bin/Debug.
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName);

// Local dev: relay the isolated Azure CLI config dir to the orchestrated children
// so their DefaultAzureCredential -> AzureCliCredential resolves the PERSONAL
// pinwiz.ai identity (where the Bicep-managed Foundry lives) instead of the machine
// default ~/.azure (which may be signed in to a different tenant). The dir is set by
// the launch config / terminal as AZURE_CONFIG_DIR=${workspaceFolder}/.azure-local;
// the AppHost simply relays whatever it received (no hardcoded path). Development-only
// — deployed children authenticate via managed identity, not the CLI, so a published
// AppHost never carries this. Without it set, children fall back to the default
// credential chain (Foundry then reports "not configured", the honest local signal).
var azureConfigDir = builder.Environment.EnvironmentName is "Development"
    ? Environment.GetEnvironmentVariable("AZURE_CONFIG_DIR")
    : null;
if (!string.IsNullOrWhiteSpace(azureConfigDir))
{
    api.WithEnvironment("AZURE_CONFIG_DIR", azureConfigDir);
    web.WithEnvironment("AZURE_CONFIG_DIR", azureConfigDir);
}

await builder.Build().RunAsync().ConfigureAwait(false);
