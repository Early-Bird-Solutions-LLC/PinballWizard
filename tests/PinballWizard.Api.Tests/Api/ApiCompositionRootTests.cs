using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Api.Middleware;
using PinballWizard.Application.Ai.Degradation;
using Xunit;

namespace PinballWizard.Api.Tests.Api;

// Composition-root regression coverage for the REAL Program.cs wiring.
//
// Unlike EndpointProblemDetailsTests (which builds a hand-rolled TestServer and
// registers IDegradationContext itself), these tests boot the actual Program.cs
// via WebApplicationFactory<Program> with every integration gate (Foundry,
// Cosmos, AI Search) forced OFF — the local-dev / Aspire-emulator shape.
//
// Regression guarded: ProblemDetailsExceptionHandler is registered
// unconditionally (AddExceptionHandler<T>), but its IDegradationContext
// dependency was previously only registered on the Foundry-wired path
// (AddAiRouter). With Foundry absent the container could not construct the
// handler — the API failed exactly the "starts cleanly in local dev" promise
// in Program.cs's own comment. IDegradationContext must now be registered
// unconditionally so the handler is constructible regardless of the gates.
public sealed class ApiCompositionRootTests
{
    // Forces all three integration gates OFF regardless of the developer's
    // appsettings / user-secrets, so the test is deterministic on any machine.
    private static WebApplicationFactory<Program> BuildUngatedFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Production env keeps ValidateOnBuild off so the assertion targets the
            // handler's own constructibility rather than eager full-graph validation.
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiFoundry:ProjectEndpoint"] = string.Empty, // foundryWired = false
                    ["Cosmos:AccountEndpoint"] = string.Empty,    // Cosmos gate off
                    ["AiSearch:Endpoint"] = string.Empty,         // AI Search gate off
                });
            });
        });
    }

    [Fact]
    public void ExceptionHandler_WhenFoundryNotWired_IsConstructible()
    {
        // The defect manifested here: resolving the unconditionally-registered
        // IExceptionHandler threw InvalidOperationException ("Unable to resolve
        // service for type IDegradationContext while attempting to activate
        // ProblemDetailsExceptionHandler") because its dependency was missing.
        using var factory = BuildUngatedFactory();

        var handler = factory.Services.GetRequiredService<IExceptionHandler>();

        Assert.IsType<ProblemDetailsExceptionHandler>(handler);
    }

    [Fact]
    public void DegradationContext_WhenFoundryNotWired_IsRegistered()
    {
        // The exception handler depends on IDegradationContext to map
        // SearchUnavailable -> 503. It must be present even without Foundry.
        using var factory = BuildUngatedFactory();

        var degradationContext = factory.Services.GetService<IDegradationContext>();

        Assert.NotNull(degradationContext);
        Assert.IsType<AmbientDegradationContext>(degradationContext);
    }
}
