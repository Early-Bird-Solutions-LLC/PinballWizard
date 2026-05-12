using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Degraded;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Degraded;

// Per ADR-0026 PR self-audit item 9(d): OutageBanner is one of the four
// locked delight surfaces (ADR-0026 § 6). The behavioral tests here assert:
//   1. Hidden by default when no degradation is active.
//   2. Renders when DegradationState is set to SearchUnavailable.
//   3. Renders mode-specific text per DegradationMode.
//   4. Dismiss button hides the banner.
//
// Pattern: tests write to IClientDegradationStore directly (the real
// implementation) — no mock needed. ClientDegradationStore is a simple
// mutable record; exercising the real implementation is both simpler and
// provides higher confidence than mocking it.
//
// ADR-0026 § 5, § 6.
public sealed class OutageBannerTests : AsyncBunitContext
{
    private readonly IClientDegradationStore _store;

    public OutageBannerTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Register the real store so tests can drive state changes.
        Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();

        // Resolve the store AFTER AddScoped — the service provider is
        // lazily built on first resolution in bUnit's BunitContext.
        _store = Services.GetRequiredService<IClientDegradationStore>();
    }

    [Fact]
    public void OutageBanner_Hidden_ByDefault()
    {
        // Act — render with no degradation active (default state).
        var cut = Render<OutageBanner>();

        // Assert — banner element is absent.
        Assert.Empty(cut.FindAll("[data-testid='outage-banner']"));
    }

    [Fact]
    public void OutageBanner_Renders_WhenSearchUnavailable()
    {
        // Arrange — set degradation state BEFORE rendering so the initial
        // render sees the non-None mode.
        _store.SetDegradation(new DegradationContext(
            DegradationMode.SearchUnavailable, Detail: null, RetryAfterSeconds: null));

        // Act
        var cut = Render<OutageBanner>();

        // Assert — banner element is present.
        cut.Find("[data-testid='outage-banner']");
    }

    [Fact]
    public void OutageBanner_Renders_AfterSetDegradation_WhenAlreadyMounted()
    {
        // Arrange — mount with no active degradation.
        var cut = Render<OutageBanner>();
        Assert.Empty(cut.FindAll("[data-testid='outage-banner']")); // confirm hidden

        // Act — push degradation state AFTER mount (simulates a live API response).
        _store.SetDegradation(new DegradationContext(
            DegradationMode.UpstreamThrottled, "Rate limit exceeded", RetryAfterSeconds: 30));

        // Assert — banner appeared after state change.
        cut.Find("[data-testid='outage-banner']");
    }

    [Theory]
    [InlineData(DegradationMode.SearchUnavailable,
        "Knowledge search is temporarily limited; community resources still work.")]
    [InlineData(DegradationMode.UpstreamThrottled,
        "AI is rate-limited; results may be slower than usual.")]
    [InlineData(DegradationMode.PartialResults,
        "Partial answer received; some sources did not load.")]
    public void OutageBanner_Renders_ModeSpecificText(DegradationMode mode, string expectedText)
    {
        // Arrange
        _store.SetDegradation(new DegradationContext(mode, Detail: null, RetryAfterSeconds: null));

        // Act
        var cut = Render<OutageBanner>();

        // Assert — the banner text matches the expected mode-specific copy.
        var bannerText = cut.Find("[data-testid='outage-banner-text']");
        Assert.Equal(expectedText, bannerText.TextContent.Trim());
    }

    [Fact]
    public void OutageBanner_DismissButton_HidesBanner()
    {
        // Arrange — show the banner.
        _store.SetDegradation(new DegradationContext(
            DegradationMode.SearchUnavailable, Detail: null, RetryAfterSeconds: null));
        var cut = Render<OutageBanner>();
        cut.Find("[data-testid='outage-banner']"); // confirm it is visible

        // Act — call Dismiss on the store (simulates user clicking the X).
        _store.Dismiss();

        // Assert — banner is hidden after dismiss.
        Assert.Empty(cut.FindAll("[data-testid='outage-banner']"));
    }
}
