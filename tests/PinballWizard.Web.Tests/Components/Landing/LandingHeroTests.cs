using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit smoke tests for LandingHero.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. LandingHero is a landing delight surface — within the
// scope of the four locked delight surfaces (ADR-0026 § 6, CLAUDE.md #14).
//
// ADR-0049 Phase 3: LandingHero now injects IMachineSuggestClient for the
// MudAutocomplete typeahead.  All tests register a no-op or fake client so
// the DI container resolves without a real HTTP server.
//
// Tests assert behavior, not structure.  Each test creates its own BunitContext
// so service registration (required before first GetService call) is explicit.
//
// Disposal: MudBlazor 9 registers PointerEventsNoneService as IAsyncDisposable
// only.  Using synchronous Dispose() on the BunitContext throws an
// InvalidOperationException.  All tests use `await using` to call DisposeAsync,
// matching the pattern in IndexPageTests.cs.
//
// MudPopoverProvider: MudBlazor 9 requires <MudPopoverProvider /> in the same
// render tree as any popover-capable component (MudAutocomplete is one).
// RenderHeroWithPopover renders them as siblings — same pattern as
// IndexPageTests.RenderIndexWithPopover.
//
// Spec conformance tests (modern-lcd.md §"Empty/landing state"):
// - Hero title renders as an ALL-CAPS display headline (PINBALLWIZARD)
// - Eyebrow tagline is present (landing-hero-eyebrow data-testid)
// - Submit adornment uses Color.Primary (observable via MudAutocomplete parameter)
public sealed class LandingHeroTests
{
    // No-op suggest client: always returns empty list.
    // Used by tests that don't need suggestion behavior to avoid constructing
    // a real HttpClient or spinning up an HTTP server.
    private static IMachineSuggestClient NoOpSuggestClient()
    {
        var client = Substitute.For<IMachineSuggestClient>();
        client.GetSuggestionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<MachineSuggestion>>([]));
        return client;
    }

    // Registers the minimum services required by LandingHero into a BunitContext.
    // Pass a custom suggestClient for tests that exercise suggestion behavior.
    private static void RegisterHeroServices(
        BunitContext ctx,
        IMachineSuggestClient? suggestClient = null)
    {
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<IMachineSuggestClient>(_ => suggestClient ?? NoOpSuggestClient());
    }

    // MudBlazor 9 requires MudPopoverProvider in the same render tree as
    // MudAutocomplete.  Render both as siblings and return the LandingHero
    // component from the fragment — mirrors IndexPageTests.RenderIndexWithPopover.
    // For tests that pass parameters to LandingHero use the raw builder pattern
    // directly (see LandingHero_OnEnterKey_InvokesQuestionSubmitted).
    private static IRenderedComponent<LandingHero> RenderHeroWithPopover(BunitContext ctx)
    {
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LandingHero>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<LandingHero>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Hero renders with a MudAutocomplete input (ADR-0049 Phase 3)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_Renders_MudAutocompleteInput()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        // MudAutocomplete is the question input since ADR-0049 Phase 3.
        cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Tagline is non-empty
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_Tagline_IsNonEmpty()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        var tagline = cut.Find("[data-testid='landing-hero-tagline']");
        Assert.False(string.IsNullOrWhiteSpace(tagline.TextContent),
            "Tagline must be non-empty — it is the prospect's first explanation of what the app does.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Question input has autofocus
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_QuestionInput_HasAutoFocus()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        // MudAutocomplete inherits AutoFocus from MudBaseInput.
        // AutoFocus=true is required so the cursor lands in the input on page load.
        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        Assert.True(autocomplete.Instance.AutoFocus,
            "LandingHero question input must have AutoFocus=true so it focuses on page load.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Hero renders the brand title in ALL CAPS (cinematic flourish)
    // Spec: modern-lcd.md §"Empty/landing state" — Barlow Condensed display
    // headline; prototype shows PINBALLWIZARD all-caps.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_Renders_BrandTitle()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        var title = cut.Find("[data-testid='landing-hero-title']");
        Assert.Contains("PinballWizard", title.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LandingHero_BrandTitle_IsAllCaps()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        // Spec: cinematic hero headline is ALL CAPS (text content is uppercase
        // in the markup, CSS also enforces text-transform: uppercase).
        var title = cut.Find("[data-testid='landing-hero-title']");
        var text = title.TextContent.Trim();
        Assert.Equal(text.ToUpperInvariant(), text);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Hero renders an eyebrow tagline (per prototype)
    // Spec: docs/ui/prototypes/empty-landing.html shows a small uppercase
    // eyebrow above the wordmark ("A COMMUNITY-RESOURCE PINBALL WIZARD").
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_Renders_EyebrowTagline()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

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
    public async Task LandingHero_SubmitAdornment_UsesColorPrimary()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        // MudAutocomplete inherits AdornmentColor from MudBaseInput.
        // AdornmentColor="Color.Primary" maps to arcade amber (#ff9a1f) via
        // PaletteDark.  Root cause of the magenta regression: missing PaletteDark
        // in PinballTheme.cs caused MudBlazor to fall back to its default dark
        // Primary (~#776be7 indigo).
        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        Assert.Equal(Color.Primary, autocomplete.Instance.AdornmentColor);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. QuestionSubmitted callback is invoked on Enter key (free-text path)
    //
    // This is the critical regression guard for the free-text submit path:
    // selecting a machine from the dropdown navigates directly, but pressing
    // Enter without a dropdown selection must still fire QuestionSubmitted
    // so the parent (Index.razor) can route to /wizard?q={text}.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnEnterKey_InvokesQuestionSubmitted()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        string? submitted = null;

        // MudBlazor 9 requires MudPopoverProvider as a sibling in the render tree.
        // Use the render-fragment builder to co-render both components and pass
        // parameters to LandingHero via attribute slots.
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LandingHero>(1);
            // EventCallback.Factory.Create requires a non-null receiver — use 'this'.
            builder.AddAttribute(2, nameof(LandingHero.QuestionText), "How does Godzilla wizard mode work?");
            builder.AddAttribute(3, nameof(LandingHero.QuestionSubmitted),
                EventCallback.Factory.Create<string>(this, q => submitted = q));
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<LandingHero>();

        // Simulate Enter key press — MudAutocomplete raises OnKeyDown. Find the
        // element INSIDE InvokeAsync so the event-handler id is resolved on the
        // same dispatcher pass it is fired on (project_bunit_dispatcher_click_pattern).
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='landing-hero-input'] input")
               .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }));

        Assert.Equal("How does Godzilla wizard mode work?", submitted);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8. Suggestion selected → navigate to /wizard?q={title}
    //
    // When the user picks a suggestion from the dropdown, the component
    // navigates directly to /wizard?q={suggestion.Title}, bypassing
    // QuestionSubmitted entirely (the title is already a resolved machine
    // name).  Verified by invoking MudAutocomplete's ValueChanged directly,
    // which is how MudBlazor fires it on item selection.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnSuggestionSelected_NavigatesToWizardWithTitle()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        // BunitNavigationManager must be resolved after render (provider locked).
        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        // Simulate the user selecting "Godzilla Pro" from the dropdown.
        // Invoking ValueChanged directly mirrors how MudAutocomplete fires
        // it on item selection (click or keyboard Enter on highlighted item).
        // NOTE: the keyboard "Enter on a highlighted suggestion" path also fires
        // OnKeyDown; the _pendingNavigationSuggestion flag suppresses the free-text
        // submit in that case. bUnit cannot faithfully simulate MudAutocomplete's
        // internal keydown→ValueChanged ordering, so that specific interleaving is
        // exercised manually, not here — this test pins the navigation outcome.
        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        await cut.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(
            new MachineSuggestion("opdb-stern-godzilla-pro", "Godzilla Pro", "Stern", 2021)));

        Assert.EndsWith("/wizard?q=Godzilla%20Pro", navMan.Uri, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 9. Suggest client returns no suggestions → no crash, no dropdown
    //
    // The IMachineSuggestClient contract is "never throw into the UI" — its
    // implementation (MachineSuggestClient) catches transport / non-200 / JSON
    // errors internally and returns []. So at the hero level the observable
    // degraded state is an EMPTY suggestion list, which must render cleanly as
    // the pre-Phase-3 no-suggestions experience (free text still works).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_WhenSuggestClientReturnsEmpty_RendersWithoutCrash()
    {
        await using var ctx = new BunitContext();

        // Client returns [] (the degraded state the client contract guarantees on
        // any backend failure). LandingHero's SearchSuggestionsAsync surfaces [] →
        // MudAutocomplete shows no dropdown; the hero must not crash.
        var emptyClient = Substitute.For<IMachineSuggestClient>();
        emptyClient.GetSuggestionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<MachineSuggestion>>([]));

        RegisterHeroServices(ctx, emptyClient);

        // Render must not throw.
        var cut = RenderHeroWithPopover(ctx);

        // Hero structure is intact.
        cut.Find("[data-testid='landing-hero']");
        cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 10. Autocomplete has MinCharacters=2 and DebounceInterval configured
    //
    // Regression guard replacing the Immediate=true test (MudTextField was
    // replaced by MudAutocomplete in ADR-0049 Phase 3).  MinCharacters=2
    // prevents a round-trip on single-character input; DebounceInterval
    // prevents a call on every keystroke.  Both are part of the
    // "polite-by-construction" posture for the suggest endpoint.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_Autocomplete_HasMinCharactersAndDebounce()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var cut = RenderHeroWithPopover(ctx);

        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        Assert.Equal(2, autocomplete.Instance.MinCharacters);
        Assert.True(autocomplete.Instance.DebounceInterval >= 200,
            "DebounceInterval must be at least 200ms to avoid a call on every keystroke.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 11. Submit adornment (arrow icon) → QuestionSubmitted with the raw text
    //
    // The adornment button is the mouse alternative to Enter for the free-text
    // path. It must fire QuestionSubmitted with the current text, independent of
    // any suggestion selection. Invoking OnAdornmentClick directly mirrors how
    // MudBlazor raises it on the adornment button click.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnAdornmentClick_InvokesQuestionSubmitted()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        string? submitted = null;

        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LandingHero>(1);
            builder.AddAttribute(2, nameof(LandingHero.QuestionText), "What is Medieval Madness worth?");
            builder.AddAttribute(3, nameof(LandingHero.QuestionSubmitted),
                EventCallback.Factory.Create<string>(this, q => submitted = q));
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<LandingHero>();

        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        await cut.InvokeAsync(() => autocomplete.Instance.OnAdornmentClick.InvokeAsync(
            new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));

        Assert.Equal("What is Medieval Madness worth?", submitted);
    }
}
