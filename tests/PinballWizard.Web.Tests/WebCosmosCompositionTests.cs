using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Hosting;
using Xunit;

namespace PinballWizard.Web.Tests;

// Coverage for the Web's Cosmos wiring gate (CosmosWebRegistration), the fix for
// the /admin data pages crashing in local-dev.
//
// Regression guarded: the Web only wired Cosmos for the deployed
// Managed-Identity path (Cosmos:AccountEndpoint). The AppHost injects the preview
// emulator via ConnectionStrings:cosmos (pinwiz-web.WithReference(cosmos)), but the
// Web neither consumed it (no AddAzureCosmosClient) nor gated on it — so locally
// ICatalogStatsReadRepository was never registered and every /admin data page
// (AdminMachines/MachineDetail) threw at DI-injection time.
//
// The gate is invoked directly on a real builder with controlled configuration,
// so it exercises the exact code Program.cs runs (a WebApplicationFactory<Program>
// test can't: factory-injected config isn't visible to Program.cs's top-level
// GetConnectionString read in minimal hosting).
public sealed class WebCosmosCompositionTests
{
    // Emulator-shaped connection string with a FAKE, obviously-not-real key.
    // Not a credential — CosmosClient construction is lazy (no network), so a
    // valid-base64 placeholder is all that is needed to exercise the wiring.
    private const string FakeAspireCosmosConnection =
        "AccountEndpoint=https://localhost:8081/;AccountKey=ZmFrZS10ZXN0LWtleS1ub3QtYS1zZWNyZXQtZmFrZQ==";

    private static IServiceProvider BuildServices(string? cosmosConnectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Deployed Managed-Identity endpoint deliberately left ABSENT (not
            // empty — CosmosOptions.AccountEndpoint is [Url]-validated and ""
            // fails it; null is allowed), mirroring local dev. The only Cosmos
            // signal under test is the Aspire connection string.
            ["ConnectionStrings:cosmos"] = cosmosConnectionString,
        });

        builder.AddWebCosmosPersistence();

        return builder.Build().Services;
    }

    [Fact]
    public void CatalogStatsRepository_WhenAspireCosmosConnectionPresent_IsRegistered()
    {
        // The fix: with the Aspire emulator connection string present, the gate
        // calls AddAzureCosmosClient + AddCatalogStatsRead, so the /admin/machines
        // dependency resolves instead of throwing at injection time.
        var services = BuildServices(FakeAspireCosmosConnection);

        Assert.NotNull(services.GetService<ICatalogStatsReadRepository>());
    }

    [Fact]
    public void CoreCosmosRepositories_WhenAspireCosmosConnectionPresent_AreRegistered()
    {
        // AdminMachineDetail also needs IMachineRepository — confirm the whole
        // AddCosmosPersistence graph registers off the connection-string path.
        var services = BuildServices(FakeAspireCosmosConnection);

        Assert.NotNull(services.GetService<IMachineRepository>());
        Assert.NotNull(services.GetService<ICatalogStatsReadRepository>());
    }

    [Fact]
    public void CatalogStatsRepository_WhenNoCosmosSignal_IsNotRegistered()
    {
        // Documents the gate: with neither the Aspire connection string nor the
        // deployed endpoint, the Cosmos block is a no-op. (In real local dev the
        // AppHost always supplies the connection string, so the admin pages get
        // their dependency; this case is a genuinely Cosmos-less run.)
        var services = BuildServices(cosmosConnectionString: null);

        Assert.Null(services.GetService<ICatalogStatsReadRepository>());
    }
}
