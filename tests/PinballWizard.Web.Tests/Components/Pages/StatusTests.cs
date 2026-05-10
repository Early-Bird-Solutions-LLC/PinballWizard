using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Landing;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test.
//
// Status.razor calls IWizardLandingClient to get SystemStatus and renders
// three MudCard status indicators (Cosmos DB, Azure AI Foundry, AI Search).
//
// Each test creates its own TestContext. Services are registered BEFORE
// any GetRequiredService or RenderComponent call (bUnit locks the provider
// on first GetService). This mirrors the IndexPageTests pattern.
//
// Tests assert:
//   1. Page renders without exception when client returns null (endpoint down).
//   2. All three status card containers are present.
//   3. When SystemStatus is all-healthy, all three indicators show "Healthy".
//   4. When the client returns null, all three indicators show "Unknown".
//   5. When SystemStatus has a false field, the affected indicator shows "Degraded".
//   6. Status heading is present.
public sealed class StatusTests
{
    // Helper — creates a substitute IWizardLandingClient returning the given response.
    private static IWizardLandingClient BuildClient(LandingResponse? response)
    {
        var client = Substitute.For<IWizardLandingClient>();
        client.GetLandingAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(response));
        return client;
    }

    // Helper — wraps SystemStatus in a minimal LandingResponse.
    private static LandingResponse BuildLandingResponse(SystemStatus status) =>
        new LandingResponse(
            SeedQuestions: [],
            FeaturedMachines: null,
            SystemStatus: status);

    // ──────────────────────────────────────────────────────────────────────
    // 1. Page renders without exception (endpoint down → null)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_Renders_WithoutException_WhenClientReturnsNull()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<IWizardLandingClient>(_ => BuildClient(response: null));

        var cut = ctx.RenderComponent<Status>();

        cut.Find("[data-testid='status-page']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. All three status cards are present
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_RendersThreeStatusCards()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<IWizardLandingClient>(_ => BuildClient(response: null));

        var cut = ctx.RenderComponent<Status>();

        cut.Find("[data-testid='status-card-cosmos']");
        cut.Find("[data-testid='status-card-foundry']");
        cut.Find("[data-testid='status-card-search']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. All-healthy SystemStatus → all three indicators show "Healthy"
    //    Behavioral: card state reflects the API response truthfully.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_AllHealthy_ShowsHealthyOnAllCards()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var allHealthy = new SystemStatus(CosmosHealthy: true, FoundryHealthy: true, AiSearchHealthy: true);
        ctx.Services.AddScoped<IWizardLandingClient>(_ =>
            BuildClient(BuildLandingResponse(allHealthy)));

        var cut = ctx.RenderComponent<Status>();

        // Wait for OnInitializedAsync to complete.
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        cut.WaitForAssertion(
            () =>
            {
                Assert.Contains("Healthy",
                    cut.Find("[data-testid='status-indicator-cosmos']").TextContent,
                    StringComparison.Ordinal);
                Assert.Contains("Healthy",
                    cut.Find("[data-testid='status-indicator-foundry']").TextContent,
                    StringComparison.Ordinal);
                Assert.Contains("Healthy",
                    cut.Find("[data-testid='status-indicator-search']").TextContent,
                    StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Client returns null → all three indicators show "Unknown"
    //    Behavioral: graceful degradation when the endpoint is unavailable.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_WhenClientReturnsNull_ShowsUnknownOnAllCards()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<IWizardLandingClient>(_ => BuildClient(response: null));

        var cut = ctx.RenderComponent<Status>();

        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        cut.WaitForAssertion(
            () =>
            {
                Assert.Contains("Unknown",
                    cut.Find("[data-testid='status-indicator-cosmos']").TextContent,
                    StringComparison.Ordinal);
                Assert.Contains("Unknown",
                    cut.Find("[data-testid='status-indicator-foundry']").TextContent,
                    StringComparison.Ordinal);
                Assert.Contains("Unknown",
                    cut.Find("[data-testid='status-indicator-search']").TextContent,
                    StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Cosmos unhealthy → Cosmos card shows "Degraded", others show "Healthy"
    //    Behavioral: per-system status is propagated correctly.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_CosmosUnhealthy_ShowsDegradedOnCosmosCard()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var partialStatus = new SystemStatus(CosmosHealthy: false, FoundryHealthy: true, AiSearchHealthy: true);
        ctx.Services.AddScoped<IWizardLandingClient>(_ =>
            BuildClient(BuildLandingResponse(partialStatus)));

        var cut = ctx.RenderComponent<Status>();

        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        cut.WaitForAssertion(
            () =>
            {
                Assert.Contains("Degraded",
                    cut.Find("[data-testid='status-indicator-cosmos']").TextContent,
                    StringComparison.Ordinal);
                Assert.Contains("Healthy",
                    cut.Find("[data-testid='status-indicator-foundry']").TextContent,
                    StringComparison.Ordinal);
                Assert.Contains("Healthy",
                    cut.Find("[data-testid='status-indicator-search']").TextContent,
                    StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. Status heading is present
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_Heading_IsPresent()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<IWizardLandingClient>(_ => BuildClient(response: null));

        var cut = ctx.RenderComponent<Status>();

        var heading = cut.Find("[data-testid='status-heading']");
        Assert.Contains("Status", heading.TextContent, StringComparison.OrdinalIgnoreCase);
    }
}
