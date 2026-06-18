using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Shared Cosmos host-wiring gate for every host (Cli / Web / Api).
//
// Each host previously inlined the same gate (Cli) or wrapped it in a
// near-identical per-host extension (CosmosWebRegistration / CosmosApiRegistration).
// This is the single source of truth: it gates on the two supported signals and,
// when wired, registers the CosmosClient (emulator or Managed-Identity) plus the
// core repositories. It returns whether Cosmos was wired so each host can gate its
// OWN dependent registrations on the same signal — Web adds catalog-stats, the Cli
// adds politeness overrides + seeders + the OPDB sync, the Api adds nothing.
public static class CosmosHostRegistration
{
    // Two supported paths:
    //  - Local dev: the AppHost wires <host>.WithReference(cosmos), injecting
    //    ConnectionStrings:cosmos (the preview emulator). AddAzureCosmosClient
    //    builds the CosmosClient from it; AddCosmosPersistence's TryAddSingleton
    //    then no-ops over that registration.
    //  - Deployed: Cosmos:AccountEndpoint is set (Bicep output); no Aspire
    //    connection string, so AddCosmosPersistence builds the Managed-Identity
    //    client itself.
    //
    // Returns true when Cosmos was wired (either signal present), false otherwise.
    // Callers gate dependent registrations on the return value rather than
    // re-reading configuration.
    public static bool AddHostCosmosPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var aspireCosmosConnection = builder.Configuration.GetConnectionString(CosmosOptions.CosmosConnectionName);
        var cosmosEndpoint = builder.Configuration[CosmosOptions.AccountEndpointKey];

        if (string.IsNullOrWhiteSpace(aspireCosmosConnection) && string.IsNullOrWhiteSpace(cosmosEndpoint))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(aspireCosmosConnection))
        {
            builder.AddAzureCosmosClient(CosmosOptions.CosmosConnectionName);
        }

        builder.Services.AddCosmosPersistence(builder.Configuration);
        return true;
    }
}
