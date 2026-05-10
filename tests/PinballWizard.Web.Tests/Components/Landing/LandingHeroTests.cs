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
// Tests assert behavior, not structure. Each test creates its own TestContext
// so service registration (required before first GetService call) is explicit.
public sealed class LandingHeroTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 1. Hero renders with a MudTextField input
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Renders_MudTextFieldInput()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<LandingHero>();

        // MudTextField should be present — it's the question input.
        cut.FindComponent<MudTextField<string>>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Tagline is non-empty
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Tagline_IsNonEmpty()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<LandingHero>();

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
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<LandingHero>();

        // MudTextField renders an input element. The component's AutoFocus
        // parameter is set to true — verify via the MudTextField component
        // parameter, as bUnit does not drive real browser focus events.
        var mudTf = cut.FindComponent<MudTextField<string>>();
        Assert.True(mudTf.Instance.AutoFocus,
            "LandingHero question input must have AutoFocus=true so it focuses on page load.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Hero renders the brand title
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingHero_Renders_BrandTitle()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.RenderComponent<LandingHero>();

        var title = cut.Find("[data-testid='landing-hero-title']");
        Assert.Contains("PinballWizard", title.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. QuestionSubmitted callback is invoked on Enter key
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnEnterKey_InvokesQuestionSubmitted()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        string? submitted = null;
        // EventCallback.Factory.Create requires a non-null receiver — use 'this'.
        var cut = ctx.RenderComponent<LandingHero>(p => p
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
