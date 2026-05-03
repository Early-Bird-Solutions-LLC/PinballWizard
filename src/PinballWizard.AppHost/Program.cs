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

await builder.Build().RunAsync().ConfigureAwait(false);
