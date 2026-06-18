using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Landing;
using PinballWizard.Application.Observability;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components.Degraded;
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
// RendererInfo context: Index.razor uses RendererInfo.IsInteractive to skip
// the API call during SSR pre-render (fast path for Lighthouse/FCP). All
// tests set IsInteractive=true to exercise the full interactive code path.
//
// Each test creates its own BunitContext. Services are registered BEFORE any
// component is rendered (bUnit locks the service provider on first GetService).
//
// Degradation tests (§ Issue #366): when the client returns null, the page
// sets DegradationMode.LandingUnavailable in the store and increments the
// pinwiz.web.landing_fallback_total OTel counter (invariant #17).
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

    // Registers all services required by Index.razor into a BunitContext.
    // Call this BEFORE locking the service provider (i.e., before calling
    // ctx.Renderer.SetRendererInfo or ctx.Render<T>).
    private static IClientDegradationStore RegisterIndexServices(
        BunitContext ctx,
        IWizardLandingClient landingClient)
    {
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<IWizardLandingClient>(_ => landingClient);
        ctx.Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();
        // ILogger<Index> — Index is the component class name; IndexPage is the
        // test alias only. Register NullLogger under the real component type.
        ctx.Services.AddSingleton<ILogger<IndexPage>>(_ => NullLogger<IndexPage>.Instance);
        ctx.Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        // Resolve store after all registrations so the DI container is built once.
        return ctx.Services.GetRequiredService<IClientDegradationStore>();
    }

    // MudBlazor 9 requires <MudPopoverProvider /> in the same render tree as
    // any popover-capable component. IndexPage renders LiveStatusBadge which
    // contains a MudTooltip. Render both as siblings in a shared fragment.
    private static IRenderedComponent<IndexPage> RenderIndexWithPopover(BunitContext ctx)
    {
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<IndexPage>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<IndexPage>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Page renders with IWizardLandingClient mocked to return known data
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WithKnownLandingResponse_RendersHeroAndGrid()
    {
        await using var ctx = new BunitContext();
        RegisterIndexServices(ctx, BuildClient(BuildLandingResponse()));

        var cut = RenderIndexWithPopover(ctx);

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
        await using var ctx = new BunitContext();
        // Simulate endpoint down — client returns null.
        RegisterIndexServices(ctx, BuildClient(response: null));

        var cut = RenderIndexWithPopover(ctx);

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
    // 3. Page shows compiled-in fallback while the API call is in-flight
    //    (changed from null/skeleton to fallback — see Index.razor §
    //    RendererInfo.IsInteractive comment for rationale)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_BeforeClientReturns_RendersFallbackNotSkeletons()
    {
        await using var ctx = new BunitContext();
        // NSubstitute returns a never-completing task to simulate "in-flight".
        var client = Substitute.For<IWizardLandingClient>();
        var tcs = new TaskCompletionSource<LandingResponse?>();
        client.GetLandingAsync(Arg.Any<CancellationToken>())
              .Returns(tcs.Task);
        RegisterIndexServices(ctx, client);

        var cut = RenderIndexWithPopover(ctx);

        // While the client is in-flight, Questions holds the compiled-in
        // fallback (not null). The fallback is set at the start of
        // OnInitializedAsync before the API await, so there is no null /
        // skeleton flash in the interactive path either.
        var grid = cut.FindComponent<SeedQuestionGrid>();
        Assert.NotNull(grid.Instance.Questions);
        Assert.Equal(4, grid.Instance.Questions!.Count);

        // Clean up the pending task to avoid hang.
        tcs.SetResult(null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. LiveStatusBadge reflects SystemStatus from the endpoint response
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WithKnownResponse_LiveStatusBadgeIsGreen()
    {
        await using var ctx = new BunitContext();
        RegisterIndexServices(ctx, BuildClient(BuildLandingResponse())); // all-true SystemStatus

        var cut = RenderIndexWithPopover(ctx);
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
        await using var ctx = new BunitContext();
        RegisterIndexServices(ctx, BuildClient(response: null));

        var cut = RenderIndexWithPopover(ctx);
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
        await using var ctx = new BunitContext();
        // Register client BEFORE locking the service provider (SetRendererInfo
        // accesses ctx.Renderer which builds the provider). BunitNavigationManager
        // is retrieved AFTER rendering to avoid locking the provider early.
        RegisterIndexServices(ctx, BuildClient(BuildLandingResponse()));

        var cut = RenderIndexWithPopover(ctx);
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // Retrieve BunitNavigationManager after render (provider already locked).
        var navMan = cut.Services.GetRequiredService<BunitNavigationManager>();

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

    // ──────────────────────────────────────────────────────────────────────
    // 7. Degradation store is set to LandingUnavailable when client returns null
    //    (Issue #366 — landing fallback must be visibly degraded, invariant #17)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WhenClientReturnsNull_SetsDegradationStoreLandingUnavailable()
    {
        await using var ctx = new BunitContext();
        var store = RegisterIndexServices(ctx, BuildClient(response: null));

        var cut = RenderIndexWithPopover(ctx);
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // The store must reflect LandingUnavailable so OutageBanner shows.
        Assert.NotNull(store.Current);
        Assert.Equal(DegradationMode.LandingUnavailable, store.Current!.Mode);
    }

    [Fact]
    public async Task Index_WhenClientReturnsNonNull_DoesNotSetDegradationStore()
    {
        await using var ctx = new BunitContext();
        var store = RegisterIndexServices(ctx, BuildClient(BuildLandingResponse()));

        var cut = RenderIndexWithPopover(ctx);
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // Healthy path: store must remain null (no degradation set).
        Assert.Null(store.Current);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8. OTel counter incremented when client returns null
    //    (Issue #366 — pinwiz.web.landing_fallback_total must fire)
    //    Pattern: MeterListener + ConcurrentBag (parallel-tolerant per
    //    project_meterlistener_test_pattern.md memory note).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_WhenClientReturnsNull_IncrementsFallbackTotalCounter()
    {
        var samples = new ConcurrentBag<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PinballWizardTelemetry.Meter.Name &&
                instrument.Name == "pinwiz.web.landing_fallback_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => samples.Add(value));
        listener.Start();

        await using var ctx = new BunitContext();
        RegisterIndexServices(ctx, BuildClient(response: null));

        var cut = RenderIndexWithPopover(ctx);
        await cut.InvokeAsync(async () => await Task.Yield());
        cut.Render();

        // Drain any pending measurements.
        listener.RecordObservableInstruments();

        Assert.Contains(samples, v => v == 1);
    }
}
