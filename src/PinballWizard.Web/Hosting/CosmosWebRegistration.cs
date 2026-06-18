using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Catalog;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Web.Hosting;

// Cosmos persistence + catalog-read wiring for the Web host.
//
// Extracted from Program.cs so the gate is directly unit-testable
// (WebCosmosCompositionTests) — a WebApplicationFactory<Program> test can't
// exercise it because config injected via the factory isn't visible to
// Program.cs's top-level GetConnectionString read in minimal hosting.
public static class CosmosWebRegistration
{
    // Wires Cosmos via the same two paths the Cli uses:
    //  - Local dev: the AppHost wires pinwiz-web.WithReference(cosmos), injecting
    //    ConnectionStrings:cosmos (the preview emulator). AddAzureCosmosClient
    //    builds the CosmosClient from it; AddCosmosPersistence's TryAddSingleton
    //    then no-ops over that registration. This is what makes the /admin data
    //    pages (AdminMachines/MachineDetail — ADR-0036) work locally instead of
    //    failing DI-injection.
    //  - Deployed: Cosmos:AccountEndpoint is set (Bicep output); no Aspire
    //    connection string, so AddCosmosPersistence builds the Managed-Identity
    //    client itself.
    // AddCatalogStatsRead registers ICatalogStatsReadRepository for /admin/machines
    // (AB#259 — ADR-0036 Tier-1 point reads). No-op only when NEITHER signal is
    // present (a genuinely Cosmos-less run).
    public static void AddWebCosmosPersistence(this IHostApplicationBuilder builder)
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
        builder.Services.AddCatalogStatsRead();
    }
}
