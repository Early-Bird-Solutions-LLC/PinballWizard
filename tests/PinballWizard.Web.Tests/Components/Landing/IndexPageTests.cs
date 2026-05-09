using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Landing;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components.Landing;
using PinballWizard.Web.Components.Pages;
using Xunit;

// Disambiguate from System.Index
using IndexPage = PinballWizard.Web.Components.Pages.Index;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit page-level tests for Index.razor (public / route).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. Per item 9 — "The page renders even if the endpoint is
// down" is a stated behavioral requirement; confirmed by the fallback test.
//
// Each test creates its own TestContext. Services are registered BEFORE any
// component is rendered (bUnit locks the service provider on first GetService).
public sealed class IndexPageTests
{
    private static LandingResponse BuildLandingResponse()
    {
        return new LandingResponse(
            SeedQuestions:
            [
                new SeedQuestion("slug-rules", "A rules question?", "Rules", "Rules desc"),
                new SeedQuestion("slug-wizard", "A wizard question?", "Wizard", "Wizard desc"),
                new SeedQuestion("slug-valuation", "A valuation question?", "Valuation", "Val desc"),
                new SeedQuestion("slug-repair", "A repair question?", "Repair", "Repair desc"),
            ],
            FeaturedMachines:
            [
                new FeaturedMachine("stern-godzilla", "Godzilla Pro", null, 1, "King of the monsters"),
            ],
            SystemStatus: new SystemStatus(CosmosHealthy: true, FoundryHealthy: true, AiSearchHealthy: true));
    }

    private static IWizardLandingClient BuildClient(LandingResponse? response)
    {
        var client = Substitute.For<IWizardLandingClient>();
        client.GetLandingAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(response));
        return client;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Page renders with IWizardLandingClient mocked to return known data
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WithKnownLandingResponse_RendersHeroAndGrid()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var client = BuildClient(BuildLandingResponse());
        ctx.Services.AddScoped<IWizardLandingClient>(_ => client);

        var cut = ctx.RenderComponent<IndexPage>();

        // Wait for OnInitializedAsync to complete (client call returns synchronously
        // via NSubstitute's Task.FromResult).
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // LandingHero is always rendered (hardcoded, no API dep).
        cut.FindComponent<LandingHero>();

        // SeedQuestionGrid should be rendered.
        cut.FindComponent<SeedQuestionGrid>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Page renders fallback when client returns null (endpoint down)
    //    ADR-0026 § Landing: "MUST work even if /api/wizard/landing is down"
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WhenClientReturnsNull_RendersFallbackSeedQuestions()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Simulate endpoint down — client returns null.
        var client = BuildClient(response: null);
        ctx.Services.AddScoped<IWizardLandingClient>(_ => client);

        var cut = ctx.RenderComponent<IndexPage>();

        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // The grid should receive the compiled-in fallback (4 questions).
        // Check the SeedQuestionGrid component's Questions parameter directly
        // to avoid selector ambiguity (seed-card-{slug} + seed-card-question
        // both match [data-testid^='seed-card-']).
        var grid = cut.FindComponent<SeedQuestionGrid>();
        Assert.NotNull(grid.Instance.Questions);
        Assert.Equal(4, grid.Instance.Questions!.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Page renders skeleton during pending state
    //    (client takes time — skeletons should be shown)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Index_BeforeClientReturns_RendersSkeletonPlaceholders()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // NSubstitute returns a never-completing task to simulate "in-flight".
        var client = Substitute.For<IWizardLandingClient>();
        var tcs = new TaskCompletionSource<LandingResponse?>();
        client.GetLandingAsync(Arg.Any<CancellationToken>())
              .Returns(tcs.Task);
        ctx.Services.AddScoped<IWizardLandingClient>(_ => client);

        var cut = ctx.RenderComponent<IndexPage>();

        // While the client is in-flight, Questions is null — grid renders skeletons.
        // Verify via the SeedQuestionGrid component parameter (null = skeleton state).
        var grid = cut.FindComponent<SeedQuestionGrid>();
        Assert.Null(grid.Instance.Questions);

        // Clean up the pending task to avoid hang.
        tcs.SetResult(null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. LiveStatusBadge reflects SystemStatus from the endpoint response
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WithKnownResponse_LiveStatusBadgeIsGreen()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var client = BuildClient(BuildLandingResponse()); // all-true SystemStatus
        ctx.Services.AddScoped<IWizardLandingClient>(_ => client);

        var cut = ctx.RenderComponent<IndexPage>();
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        cut.WaitForAssertion(
            () =>
            {
                var badge = cut.FindComponent<LiveStatusBadge>();
                var dot = badge.Find("[data-testid='live-status-dot']");
                Assert.Contains("status-dot--green", dot.GetAttribute("class") ?? string.Empty,
                    StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. LiveStatusBadge is amber when client returns null (status unknown)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WhenClientReturnsNull_LiveStatusBadgeIsAmber()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var client = BuildClient(response: null);
        ctx.Services.AddScoped<IWizardLandingClient>(_ => client);

        var cut = ctx.RenderComponent<IndexPage>();
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        cut.WaitForAssertion(
            () =>
            {
                var badge = cut.FindComponent<LiveStatusBadge>();
                var dot = badge.Find("[data-testid='live-status-dot']");
                Assert.Contains("status-dot--amber", dot.GetAttribute("class") ?? string.Empty,
                    StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. Clicking a seed question card navigates to /wizard/q/{slug}
    //    Behavioral pin: click → actual NavigationManager URI change
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_OnSeedCardClick_NavigatesToWizardQ()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Register client BEFORE retrieving any service (bUnit locks provider
        // on first GetService call). FakeNavigationManager is retrieved AFTER
        // rendering to avoid locking the provider before AddScoped runs.
        var client = BuildClient(BuildLandingResponse());
        ctx.Services.AddScoped<IWizardLandingClient>(_ => client);

        var cut = ctx.RenderComponent<IndexPage>();
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // Retrieve FakeNavigationManager after render (provider already locked).
        var navMan = cut.Services.GetRequiredService<FakeNavigationManager>();

        cut.WaitForAssertion(
            () =>
            {
                var card = cut.Find("[data-testid='seed-card-slug-rules']");
                Assert.NotNull(card);
            },
            timeout: TimeSpan.FromSeconds(3));

        // Click the first card (slug from BuildLandingResponse: "slug-rules").
        var firstCard = cut.Find("[data-testid='seed-card-slug-rules']");
        await cut.InvokeAsync(() => firstCard.Click());

        Assert.EndsWith("/wizard/q/slug-rules", navMan.Uri, StringComparison.Ordinal);
    }
}
