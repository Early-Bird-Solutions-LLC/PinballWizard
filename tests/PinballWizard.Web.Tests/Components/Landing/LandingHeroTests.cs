using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit smoke tests for LandingHero.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. LandingHero is a landing delight surface — within the
// scope of the four locked delight surfaces (ADR-0026 § 6, CLAUDE.md #14).
//
// Tests assert behavior, not structure. Each test creates its own BunitContext
// so service registration (required before first GetService call) is explicit.
//
// Spec conformance tests (modern-lcd.md §"Empty/landing state"):
// - Hero title renders as an ALL-CAPS display headline (PINBALLWIZARD)
// - Eyebrow tagline is present (landing-hero-eyebrow data-testid)
// - Submit adornment uses Color.Primary (observable via MudTextField parameter)
public sealed class LandingHeroTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 1. Hero renders with a MudTextField input
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Renders_MudTextFieldInput()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        // MudTextField should be present — it's the question input.
        cut.FindComponent<MudTextField<string>>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Tagline is non-empty
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Tagline_IsNonEmpty()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        var tagline = cut.Find("[data-testid='landing-hero-tagline']");
        Assert.False(string.IsNullOrWhiteSpace(tagline.TextContent),
            "Tagline must be non-empty — it is the prospect's first explanation of what the app does.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Question input has autofocus
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_QuestionInput_HasAutoFocus()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        // MudTextField renders an input element. The component's AutoFocus
        // parameter is set to true — verify via the MudTextField component
        // parameter, as bUnit does not drive real browser focus events.
        var mudTf = cut.FindComponent<MudTextField<string>>();
        Assert.True(mudTf.Instance.AutoFocus,
            "LandingHero question input must have AutoFocus=true so it focuses on page load.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Hero renders the brand title in ALL CAPS (cinematic flourish)
    // Spec: modern-lcd.md §"Empty/landing state" — Barlow Condensed display
    // headline; prototype shows PINBALLWIZARD all-caps.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Renders_BrandTitle()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        var title = cut.Find("[data-testid='landing-hero-title']");
        Assert.Contains("PinballWizard", title.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LandingHero_BrandTitle_IsAllCaps()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        // Spec: cinematic hero headline is ALL CAPS (text content is uppercase
        // in the markup, CSS also enforces text-transform: uppercase).
        var title = cut.Find("[data-testid='landing-hero-title']");
        var text = title.TextContent.Trim();
        // Spec: cinematic hero headline is ALL CAPS (text content is uppercase
        // in the markup, CSS also enforces text-transform: uppercase).
        Assert.Equal(text.ToUpperInvariant(), text);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Hero renders an eyebrow tagline (per prototype)
    // Spec: docs/ui/prototypes/empty-landing.html shows a small uppercase
    // eyebrow above the wordmark ("A COMMUNITY-RESOURCE PINBALL WIZARD").
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Renders_EyebrowTagline()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        // The eyebrow element sits above the wordmark; aria-hidden so it
        // doesn't duplicate the title for screen readers.
        var eyebrow = cut.Find("[data-testid='landing-hero-eyebrow']");
        Assert.False(string.IsNullOrWhiteSpace(eyebrow.TextContent),
            "Eyebrow must be non-empty — it positions the brand identity above the wordmark.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. Submit adornment uses Color.Primary (accent-primary amber)
    // Root cause of the magenta submit: PaletteDark was missing from
    // PinballTheme, so MudBlazor fell back to its default dark Primary
    // (~#776be7 indigo/violet). The fix is PaletteDark in PinballTheme.cs.
    // This test pins the Color.Primary on the adornment so a regression
    // (e.g., someone setting AdornmentColor="Color.Secondary") is caught.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_SubmitAdornment_UsesColorPrimary()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LandingHero>();

        // The MudTextField's AdornmentColor parameter is observable via the instance.
        // AdornmentColor="Color.Primary" maps to arcade amber (#ff9a1f) via PaletteDark.
        // Root cause of the magenta regression: missing PaletteDark in PinballTheme.cs
        // caused MudBlazor to fall back to its default dark Primary (~#776be7 indigo).
        var mudTf = cut.FindComponent<MudTextField<string>>();
        Assert.Equal(Color.Primary, mudTf.Instance.AdornmentColor);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. QuestionSubmitted callback is invoked on Enter key
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnEnterKey_InvokesQuestionSubmitted()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        string? submitted = null;
        // EventCallback.Factory.Create requires a non-null receiver — use 'this'.
        var cut = ctx.Render<LandingHero>(p => p
            .Add(h => h.QuestionText, "How does Godzilla wizard mode work?")
            .Add(h => h.QuestionSubmitted, EventCallback.Factory.Create<string>(
                this, q => submitted = q)));

        // Simulate Enter key press — MudTextField raises OnKeyDown.
        var input = cut.Find("[data-testid='landing-hero-input'] input");
        await cut.InvokeAsync(() => input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs
        {
            Key = "Enter",
        }));

        Assert.Equal("How does Godzilla wizard mode work?", submitted);
    }
}
