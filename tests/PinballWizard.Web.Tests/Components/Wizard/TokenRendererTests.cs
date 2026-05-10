using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Behavioral tests for TokenRenderer.
//
// Per ADR-0026 PR self-audit item 9(d): every new Razor component must have
// a bUnit smoke test. TokenRenderer is a sub-component of the WizardAnswerStream
// locked delight surface.
//
// Tests assert:
//   1. Empty SpanTexts renders nothing (no token-renderer div).
//   2. Non-empty SpanTexts renders all tokens as individual spans.
//   3. The newest span gets the bumper-pulse CSS class when IsStreaming=true.
//   4. When IsStreaming=false, no span gets the bumper-pulse class.
//   5. prefers-reduced-motion: the bumper-pulse class is present on the newest
//      span — CSS @media handles the motion suppression; we assert the class
//      structure so the media query has the correct selector target.
//   6. aria-live="polite" is present for screen-reader announcements.
public sealed class TokenRendererTests
{
    private static TestContext BuildCtx()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Empty SpanTexts renders nothing
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_SpanTexts_renders_nothing()
    {
        using var ctx = BuildCtx();
        var cut = ctx.RenderComponent<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, Array.Empty<string>()));

        Assert.Empty(cut.FindAll("[data-testid='token-renderer']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Non-empty SpanTexts renders token-renderer div + all token spans
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Non_empty_SpanTexts_renders_all_spans()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "Stern ", "Godzilla ", "Pro" };

        var cut = ctx.RenderComponent<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, false));

        cut.Find("[data-testid='token-renderer']");
        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(3, spans.Count);

        Assert.Equal("Stern ", spans[0].TextContent);
        Assert.Equal("Godzilla ", spans[1].TextContent);
        Assert.Equal("Pro", spans[2].TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // IsStreaming=true: newest span carries bumper-pulse class
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void When_IsStreaming_newest_span_carries_bumper_pulse_class()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "Token A", "Token B", "Token C" };

        var cut = ctx.RenderComponent<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, true));

        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(3, spans.Count);

        // Only the last span gets the --new modifier class.
        Assert.DoesNotContain("token-renderer__span--new", spans[0].ClassName ?? string.Empty);
        Assert.DoesNotContain("token-renderer__span--new", spans[1].ClassName ?? string.Empty);
        Assert.Contains("token-renderer__span--new", spans[2].ClassName ?? string.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────
    // IsStreaming=false: no span carries bumper-pulse class
    //
    // Once Final lands, WizardAnswerStream sets IsStreaming=false.
    // The canonical text is rendered as a single span with no animation.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void When_not_IsStreaming_no_span_carries_bumper_pulse_class()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "Canonical answer text" };

        var cut = ctx.RenderComponent<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, false));

        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Single(spans);
        Assert.DoesNotContain("token-renderer__span--new", spans[0].ClassName ?? string.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────
    // prefers-reduced-motion: the --new class is present (CSS handles suppression)
    //
    // The CSS @media (prefers-reduced-motion: reduce) rule targets
    // .token-renderer__span--new and overrides animation: none !important.
    // We assert the class is present so the media query has the correct
    // selector target. The actual suppression is a CSS responsibility.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Newest_span_class_structure_supports_prefers_reduced_motion_targeting()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "First token", "Second token" };

        var cut = ctx.RenderComponent<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, true));

        // The newest span carries the --new modifier that the CSS media query targets.
        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(2, spans.Count);
        Assert.Contains("token-renderer__span--new", spans[1].ClassName ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // aria-live="polite" on the token-renderer container
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Token_renderer_has_aria_live_polite()
    {
        using var ctx = BuildCtx();
        string[] spans = ["Hello"];
        var cut = ctx.RenderComponent<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, spans)
            .Add(c => c.IsStreaming, false));

        var container = cut.Find("[data-testid='token-renderer']");
        Assert.Equal("polite", container.GetAttribute("aria-live"));
    }
}
