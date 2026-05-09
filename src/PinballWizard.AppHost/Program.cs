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
    .WithReference(cosmos);

// Blazor Web App — Wave 1 PR-F0 (amended by PR-F2). Wired with a Cosmos
// reference and the Api reference so Aspire injects the service-discovery
// env vars (services__pinwiz-api__http__0) that WizardStreamingClient
// resolves via "https+http://pinwiz-api". See ADR-0026 § 1/2.
//
// NOTE: The Cosmos preview emulator comment at the top of this file
// documents why WaitFor is not chained here — dependency ordering is
// handled at the application layer (CosmosBootstrapper.EnsureCreatedAsync).
_ = builder.AddProject<Projects.PinballWizard_Web>("pinwiz-web")
    .WithReference(cosmos)
    .WithReference(api);

await builder.Build().RunAsync().ConfigureAwait(false);
