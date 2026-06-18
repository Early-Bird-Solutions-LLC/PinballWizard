using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Infrastructure.Catalog;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Web.Hosting;

// Cosmos persistence + catalog-read wiring for the Web host.
//
// Delegates the shared host gate to CosmosHostRegistration.AddHostCosmosPersistence
// and layers on the Web-only AddCatalogStatsRead when Cosmos is wired — that
// registers ICatalogStatsReadRepository for the /admin/machines pages
// (AB#259 — ADR-0036), which would otherwise fail DI-injection locally.
//
// Kept as a thin Web extension (rather than calling the shared helper inline in
// Program.cs) so the catalog-stats addition stays directly unit-testable
// (WebCosmosCompositionTests) — a WebApplicationFactory<Program> test can't
// exercise it because factory-injected config isn't visible to Program.cs's
// top-level config read in minimal hosting.
public static class CosmosWebRegistration
{
    public static void AddWebCosmosPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.AddHostCosmosPersistence())
        {
            builder.Services.AddCatalogStatsRead();
        }
    }
}
