using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Api.Hosting;

// Cosmos persistence wiring for the Api host.
//
// Extracted from Program.cs so the gate is directly unit-testable
// (ApiCosmosCompositionTests) — a WebApplicationFactory<Program> test can't
// exercise it because config injected via the factory isn't visible to
// Program.cs's top-level GetConnectionString read in minimal hosting.
//
// Mirrors PinballWizard.Web's CosmosWebRegistration (which additionally wires
// catalog-stats for the admin UI — the Api needs only the core repositories
// for the Wizard's getMachineByTitle grounding tool). A future cleanup could
// hoist this shared gate into Infrastructure so Cli/Web/Api share one helper.
public static class CosmosApiRegistration
{
    // Two supported paths, mirroring the Cli:
    //  - Local dev: the AppHost wires pinwiz-api.WithReference(cosmos), injecting
    //    ConnectionStrings:cosmos (the preview emulator). AddAzureCosmosClient
    //    builds the CosmosClient from it; AddCosmosPersistence's TryAddSingleton
    //    then no-ops over that registration. So when Foundry is also wired
    //    locally, the AiRouter's MachineGroundingTool (→ IMachineRepository)
    //    resolves against the emulator instead of failing.
    //  - Deployed: Cosmos:AccountEndpoint is set (Bicep output); no Aspire
    //    connection string, so AddCosmosPersistence builds the Managed-Identity
    //    client itself.
    // No-op only when NEITHER signal is present (a genuinely Cosmos-less run —
    // e.g. unit tests, or local dev before any Cosmos hand-off).
    public static void AddApiCosmosPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var aspireCosmosConnection = builder.Configuration.GetConnectionString(CosmosOptions.CosmosConnectionName);
        var cosmosEndpoint = builder.Configuration[CosmosOptions.AccountEndpointKey];

        if (string.IsNullOrWhiteSpace(aspireCosmosConnection) && string.IsNullOrWhiteSpace(cosmosEndpoint))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(aspireCosmosConnection))
        {
            builder.AddAzureCosmosClient(CosmosOptions.CosmosConnectionName);
        }

        builder.Services.AddCosmosPersistence(builder.Configuration);
    }
}
