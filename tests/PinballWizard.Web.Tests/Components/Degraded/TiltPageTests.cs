using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Degraded;
using PinballWizard.Web.Components.Pages;
using Xunit;

// Alias Error to TiltPage so tests read as "TiltPage" semantically.
// The component lives in Error.razor (the Wave 1 placeholder expanded by
// Wave 2 PR-D-degraded) but is architecturally named TiltPage.
using TiltPage = PinballWizard.Web.Components.Pages.Error;

namespace PinballWizard.Web.Tests.Components.Degraded;

// Per ADR-0026 PR self-audit item 9(d): TiltPage (/error + /tilt routes)
// is one of the four locked delight surfaces (ADR-0026 § 6). The behavioral
// tests here assert:
//   1. TILT message renders.
//   2. requestId from query parameter populates the DMD score line.
//   3. "Try Again" button renders.
//   4. CSS class structure that gates the prefers-reduced-motion @media rule
//      is present — structural pin so the @media rule cannot be silently dropped.
//
// Note on prefers-reduced-motion test:
//   CSS @media queries are browser-evaluated at render time and cannot be
//   unit-tested behaviourally via bUnit (no browser). The test instead
//   asserts the element with class "tilt-animated-wrapper" is present, which
//   is the CSS selector the @media rule targets. If the class is renamed or
//   removed, the test fails — ensuring the rule in Error.razor.css stays
//   connected to a real element. This is a structural pin, not a CSS-execution
//   test. The comment is explicit so a future reviewer doesn't misread it as
//   "we tested the animation is actually disabled."
//
// ADR-0026 § 5, § 6, § 9.
public sealed class TiltPageTests : AsyncBunitContext
{
    public TiltPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        // OutageBanner (mounted in MainLayout) injects IClientDegradationStore.
        // TiltPage itself doesn't inject it, but registering avoids DI errors
        // when IClientDegradationStore is needed by the component tree.
        Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();

        // bUnit registers BunitNavigationManager automatically.
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void TiltPage_Renders_TiltHeading()
    {
        // Act
        var cut = Render<TiltPage>();

        // Assert — the TILT heading text is present.
        Assert.Contains("TILT", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TiltPage_Renders_TiltHeadingElement()
    {
        var cut = Render<TiltPage>();

        // Assert — the element with data-testid="tilt-heading" exists.
        var heading = cut.Find("[data-testid='tilt-heading']");
        Assert.Contains("TILT", heading.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TiltPage_Renders_RequestId_FromQueryParameter()
    {
        // Arrange — set a requestId query parameter on the navigation URL.
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("http://localhost/error?requestId=abc-test-123&reason=Timeout");

        // Act
        var cut = Render<TiltPage>();

        // Assert — the DMD strip contains the request-id from the query param.
        var dmdValue = cut.Find("[data-testid='tilt-request-id']");
        Assert.Equal("abc-test-123", dmdValue.TextContent.Trim());
    }

    [Fact]
    public void TiltPage_TryAgain_IsAnchorToWizard_NotAClickHandler()
    {
        var cut = Render<TiltPage>();

        // Error surfaces stay static (no circuit dependency). The "Try Again"
        // control must be a real anchor to /wizard, not an OnClick handler that
        // is dead on a statically-rendered error page (ADR-0034 amendment).
        var tryAgain = cut.Find("[data-testid='tilt-try-again']");
        Assert.Equal("a", tryAgain.TagName, ignoreCase: true);
        Assert.Equal("/wizard", tryAgain.GetAttribute("href"));
    }

    [Fact]
    public void TiltPage_Renders_BackToHome_Button()
    {
        var cut = Render<TiltPage>();

        // Assert — the "Back to Home" link-button is rendered.
        var button = cut.Find("[data-testid='tilt-home']");
        Assert.NotNull(button);
    }

    [Fact]
    public void TiltPage_Renders_Reason_ChipWhenPresent()
    {
        // Arrange
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("http://localhost/error?reason=SearchUnavailable");

        // Act
        var cut = Render<TiltPage>();

        // Assert — reason chip is visible when query param is supplied.
        var chip = cut.Find("[data-testid='tilt-reason']");
        Assert.Contains("SearchUnavailable", chip.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Structural pin: asserts the element with CSS class "tilt-animated-wrapper"
    /// is present in the rendered markup.
    ///
    /// This matters because the scoped CSS (Error.razor.css) targets
    /// ".tilt-animated-wrapper" in its @media (prefers-reduced-motion: reduce)
    /// rule. If the class is renamed or removed, the @media rule silently stops
    /// working — the animation would play for ALL users, including those with
    /// vestibular disorders or motion sensitivity.
    ///
    /// This test does NOT assert that the animation is visually disabled
    /// (CSS media queries are browser-evaluated, not unit-testable). It asserts
    /// that the structural contract — the class the rule targets — exists in the
    /// rendered output.
    /// </summary>
    [Fact]
    public void Tilt_animation_disabled_when_prefers_reduced_motion()
    {
        // Act
        var cut = Render<TiltPage>();

        // Assert — the element carrying the animation class is present.
        // The @media (prefers-reduced-motion: reduce) rule in Error.razor.css
        // sets animation: none on this class. If the class is removed, this
        // test fails and the PR is blocked — ensuring the @media rule stays
        // connected to a real DOM element.
        var animatedWrapper = cut.Find("[data-testid='tilt-animated-wrapper']");
        Assert.Contains("tilt-animated-wrapper", animatedWrapper.ClassName ?? string.Empty);
    }
}
