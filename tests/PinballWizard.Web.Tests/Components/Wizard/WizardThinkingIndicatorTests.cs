using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Behavioral tests for WizardThinkingIndicator.
//
// Per ADR-0026 PR self-audit item 9(d): every new Razor component must have
// a bUnit smoke test. WizardThinkingIndicator is a sub-component of the
// WizardAnswerStream locked delight surface.
//
// Tests assert:
//   1. The component renders with the correct ARIA role and aria-label for
//      screen-reader accessibility.
//   2. The three animated dots are present in the DOM (structural pin to
//      ensure the CSS animation classes can apply to real elements).
//   3. prefers-reduced-motion is respected via CSS class structure — the
//      dots carry individual delay classes so the CSS @media query can
//      target them; we assert the data-testid and the class structure.
public sealed class WizardThinkingIndicatorTests
{
    private static TestContext BuildCtx()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Renders with ARIA role=status and aria-label for accessibility
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_with_aria_role_status_and_accessible_label()
    {
        using var ctx = BuildCtx();
        var cut = ctx.RenderComponent<WizardThinkingIndicator>();

        var indicator = cut.Find("[data-testid='wizard-thinking-indicator']");

        Assert.Equal("status", indicator.GetAttribute("role"));
        Assert.Equal("Wizard is thinking", indicator.GetAttribute("aria-label"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Renders three animated dot elements (one per delay class)
    //
    // The three dots carry --1, --2, --3 BEM modifier classes. The CSS
    // animation uses animation-delay on each modifier class. We pin the
    // structural presence of all three so the CSS rules have real targets.
    // If a future edit collapses the dots to one element, this test catches it.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_three_animation_dots_with_stagger_classes()
    {
        using var ctx = BuildCtx();
        var cut = ctx.RenderComponent<WizardThinkingIndicator>();

        // All three delay-modifier classes must be present.
        Assert.Single(cut.FindAll(".wizard-thinking-indicator__dot--1"));
        Assert.Single(cut.FindAll(".wizard-thinking-indicator__dot--2"));
        Assert.Single(cut.FindAll(".wizard-thinking-indicator__dot--3"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // prefers-reduced-motion: dots carry the base class for CSS targeting
    //
    // The CSS @media (prefers-reduced-motion: reduce) rule targets
    // .wizard-thinking-indicator__dot and overrides animation: none.
    // We assert the base class is present on every dot so the media
    // query has the correct selector targets. The actual animation
    // suppression is a CSS responsibility (not assertable in bUnit),
    // but the class structure is the contract between the component
    // and the stylesheet.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_dots_carry_base_class_for_prefers_reduced_motion_targeting()
    {
        using var ctx = BuildCtx();
        var cut = ctx.RenderComponent<WizardThinkingIndicator>();

        // All three elements must have the base dot class.
        var dots = cut.FindAll(".wizard-thinking-indicator__dot");
        Assert.Equal(3, dots.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Dots are aria-hidden so screen readers don't read the decorative dots
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dot_container_is_aria_hidden()
    {
        using var ctx = BuildCtx();
        var cut = ctx.RenderComponent<WizardThinkingIndicator>();

        var dotsContainer = cut.Find(".wizard-thinking-indicator__dots");
        Assert.Equal("true", dotsContainer.GetAttribute("aria-hidden"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // "Wizard is thinking" label text is present for screen readers
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_thinking_label_text()
    {
        using var ctx = BuildCtx();
        var cut = ctx.RenderComponent<WizardThinkingIndicator>();

        Assert.Contains("Wizard is thinking", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
