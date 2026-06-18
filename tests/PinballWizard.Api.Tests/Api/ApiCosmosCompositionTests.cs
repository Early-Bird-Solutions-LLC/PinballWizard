using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Api.Hosting;
using PinballWizard.Application.Persistence;
using Xunit;

namespace PinballWizard.Api.Tests.Api;

// Coverage for the Api's Cosmos wiring gate (CosmosApiRegistration) — parity
// with the Web's CosmosWebRegistration fix (PR #433).
//
// Regression guarded: the Api only wired Cosmos for the deployed
// Managed-Identity path (Cosmos:AccountEndpoint). The AppHost injects the
// preview emulator via ConnectionStrings:cosmos (pinwiz-api.WithReference(cosmos)),
// but the Api neither consumed it (no AddAzureCosmosClient) nor gated on it — so
// with Foundry wired locally, the AiRouter's MachineGroundingTool (→
// IMachineRepository) would fail to resolve.
//
// The gate is invoked directly on a real builder with controlled configuration,
// exercising the exact code Program.cs runs (a WebApplicationFactory<Program>
// test can't: factory-injected config isn't visible to Program.cs's top-level
// GetConnectionString read in minimal hosting).
public sealed class ApiCosmosCompositionTests
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

        builder.AddApiCosmosPersistence();

        return builder.Build().Services;
    }

    [Fact]
    public void MachineRepository_WhenAspireCosmosConnectionPresent_IsRegistered()
    {
        // The fix: with the Aspire emulator connection string present, the gate
        // calls AddAzureCosmosClient + AddCosmosPersistence, so the grounding
        // tool's IMachineRepository resolves instead of failing.
        var services = BuildServices(FakeAspireCosmosConnection);

        Assert.NotNull(services.GetService<IMachineRepository>());
    }

    [Fact]
    public void MachineRepository_WhenNoCosmosSignal_IsNotRegistered()
    {
        // Documents the gate: with neither the Aspire connection string nor the
        // deployed endpoint, the Cosmos block is a no-op (the Api starts clean).
        var services = BuildServices(cosmosConnectionString: null);

        Assert.Null(services.GetService<IMachineRepository>());
    }
}
