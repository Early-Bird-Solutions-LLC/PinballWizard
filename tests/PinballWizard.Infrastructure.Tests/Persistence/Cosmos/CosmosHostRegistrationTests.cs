using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

// Canonical coverage for the shared host Cosmos gate (CosmosHostRegistration),
// used by Cli/Web/Api. The gate is invoked directly on a real builder with
// controlled config — exercising the exact code each host's Program.cs runs (a
// WebApplicationFactory<Program> test can't: factory-injected config isn't
// visible to Program.cs's top-level GetConnectionString read in minimal hosting).
//
// Regression guarded: hosts previously gated only on Cosmos:AccountEndpoint and
// ignored the Aspire-injected ConnectionStrings:cosmos (the local emulator), so
// the core repositories went unregistered locally.
public sealed class CosmosHostRegistrationTests
{
    // Emulator-shaped connection string with a FAKE, obviously-not-real key.
    // Not a credential — CosmosClient construction is lazy (no network), so a
    // valid-base64 placeholder is all that is needed to exercise the wiring.
    private const string FakeAspireCosmosConnection =
        "AccountEndpoint=https://localhost:8081/;AccountKey=ZmFrZS10ZXN0LWtleS1ub3QtYS1zZWNyZXQtZmFrZQ==";

    private static (bool Wired, IServiceProvider Services) Run(string? cosmosConnectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Deployed Managed-Identity endpoint deliberately left ABSENT (not
            // empty — CosmosOptions.AccountEndpoint is [Url]-validated and ""
            // fails it; null is allowed). The only signal under test is the
            // Aspire connection string.
            ["ConnectionStrings:cosmos"] = cosmosConnectionString,
        });

        var wired = builder.AddHostCosmosPersistence();
        return (wired, builder.Build().Services);
    }

    [Fact]
    public void AddHostCosmosPersistence_WhenAspireConnectionPresent_WiresAndRegistersRepositories()
    {
        var (wired, services) = Run(FakeAspireCosmosConnection);

        Assert.True(wired);
        Assert.NotNull(services.GetService<IMachineRepository>());
    }

    [Fact]
    public void AddHostCosmosPersistence_WhenNoCosmosSignal_ReturnsFalseAndRegistersNothing()
    {
        var (wired, services) = Run(cosmosConnectionString: null);

        Assert.False(wired);
        Assert.Null(services.GetService<IMachineRepository>());
    }
}
