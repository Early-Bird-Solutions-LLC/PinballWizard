using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
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

    [Fact]
    public void AddHostCosmosPersistence_AspireClient_UsesCustomSystemTextJsonSerializer()
    {
        // Regression guard for the local-emulator write-400 (2026-06-18).
        // The CosmosClient that Aspire's AddAzureCosmosClient builds for the
        // emulator MUST carry PinballWizard's System.Text.Json serializer. The
        // SDK default serializer ignores the documents' [JsonPropertyName]
        // attributes, so the partition key serialises under the wrong name
        // (PascalCase) and the gateway rejects every write with 400 BadRequest
        // (RU=0). The fix is the configureClientOptions argument in
        // CosmosHostRegistration; without it, local seeding + every admin/RAG
        // write silently fail against the emulator. The Managed-Identity
        // fallback path already applied the serializer, which is why live Cosmos
        // worked and only the emulator broke.
        var (_, services) = Run(FakeAspireCosmosConnection);

        var client = services.GetRequiredService<CosmosClient>();

        Assert.IsType<SystemTextJsonCosmosSerializer>(client.ClientOptions.Serializer);

        // Behavior, not just type: the actual failure was the partition key
        // serialising as "PartitionKey" (PascalCase, SDK default) instead of the
        // "/partitionKey" path the container expects. Serialise a real document
        // through the wired serializer and assert the camelCase name the gateway
        // requires — this is what fails with 400 when the serializer is wrong.
        using var stream = client.ClientOptions.Serializer.ToStream(new IngestionSource
        {
            Id = "stern",
            DisplayName = "Stern Pinball",
            ScraperImplKey = "stern",
            BaseUrl = "https://example.com",
            Cadence = "daily",
        });
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        Assert.Contains("\"partitionKey\"", json);
        Assert.Contains("\"id\"", json);
        Assert.DoesNotContain("\"PartitionKey\"", json);
    }
}
