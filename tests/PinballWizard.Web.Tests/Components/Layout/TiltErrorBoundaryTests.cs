using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Theming;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// Per ADR-0026 PR self-audit item 9(d): TiltErrorBoundary is one of the four
// locked delight surfaces (ADR-0026 § 6). The behavioral test here asserts
// the boundary's fallback content actually renders when a child throws.
//
// Pattern: bUnit's ErrorBoundary testing requires a child component that throws
// on render; the boundary must catch it and render its fallback template.
//
// ADR-0026 § 6 — TiltErrorBoundary is a delight surface (custom component).
// ADR-0026 § 9 — requestId (TraceId) surfaced in fallback.
public sealed class TiltErrorBoundaryTests : AsyncBunitContext
{
    public TiltErrorBoundaryTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void TiltErrorBoundary_PassesThrough_ChildContentWhenNoException()
    {
        var cut = Render<TiltErrorBoundary>(parameters => parameters
            .AddChildContent("<span data-testid='child'>healthy child</span>"));

        // Assert — child content renders when no exception is active.
        cut.Find("[data-testid='child']");
    }

    [Fact]
    public void TiltErrorBoundary_ShowsTiltFallback_WhenChildThrows()
    {
        // Arrange — render a child that throws synchronously on first render.
        var cut = Render<TiltErrorBoundary>(parameters => parameters
            .AddChildContent<ThrowingComponent>());

        // Act — the ErrorBoundary catches the exception during render.
        // bUnit 1.37.7: exception is swallowed by the ErrorBoundary;
        // we assert the fallback is visible.

        // Assert — "TILT" heading is rendered.
        Assert.Contains("TILT", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // Assert — request-id span is present (may read "no-trace" in test context
        // since Activity.Current is null without OTel pipeline, which is expected).
        cut.Find("[data-testid='tilt-request-id']");
    }

    [Fact]
    public void TiltErrorBoundary_Recovery_IsStaticSafeAnchor_NotAClickHandler()
    {
        var cut = Render<TiltErrorBoundary>(parameters => parameters
            .AddChildContent<ThrowingComponent>());

        // The boundary can trip on a statically-hosted page where OnClick is dead.
        // Recovery must be a real anchor (full reload of the current URI), not a
        // circuit-dependent click handler (ADR-0034 amendment §3.4).
        var recover = cut.Find("[data-testid='tilt-recover']");
        Assert.Equal("a", recover.TagName, ignoreCase: true);
        Assert.Equal("false", recover.GetAttribute("data-enhance-nav"));
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A Razor component that throws a predictable exception on first render,
    /// used to exercise TiltErrorBoundary's catch path in tests.
    /// </summary>
    private sealed class ThrowingComponent : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(
            Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            throw new InvalidOperationException("Test-induced render failure.");
        }
    }
}
