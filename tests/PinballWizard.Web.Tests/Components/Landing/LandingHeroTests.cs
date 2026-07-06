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
    // 8. Suggestion selected for a NAME query → navigate to /wizard?q={title}
    //
    // When the user is typing a machine NAME ("Godzilla") and picks a matching
    // suggestion, the component treats it as a machine jump: navigate directly
    // to /wizard?q={suggestion.Title}, bypassing QuestionSubmitted.  The intent
    // test is "does the title start with what the user typed" — here it does.
    // Verified by invoking MudAutocomplete's ValueChanged directly, which is how
    // MudBlazor fires it on item selection.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnSuggestionSelected_ForNameQuery_NavigatesToWizardWithTitle()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        // The user typed a machine name ("Godzilla"); the suggestion title
        // ("Godzilla Pro") starts with it → genuine machine jump.  Wire
        // QuestionSubmitted so we can assert the machine-jump path does NOT also
        // fire the free-text submit (no double-handling of the same selection).
        string? submitted = null;
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LandingHero>(1);
            builder.AddAttribute(2, nameof(LandingHero.QuestionText), "Godzilla");
            builder.AddAttribute(3, nameof(LandingHero.QuestionSubmitted),
                EventCallback.Factory.Create<string>(this, q => submitted = q));
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<LandingHero>();

        // BunitNavigationManager must be resolved after render (provider locked).
        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        await cut.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(
            new MachineSuggestion("opdb-stern-godzilla-pro", "Godzilla Pro", "Stern", 2021)));

        Assert.EndsWith("/wizard?q=Godzilla%20Pro", navMan.Uri, StringComparison.Ordinal);
        Assert.Null(submitted); // machine jump only — never also a free-text submit
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8a. Interior-word name query: an article-prefixed title ("The Addams
    //     Family") is still a machine jump when the user types the distinctive
    //     word ("addams").  Without the interior-word branch, StartsWith alone
    //     would misroute ~a quarter of the catalog (every "The X" title) into
    //     the free-text path.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_OnSuggestionSelected_ForInteriorWordNameQuery_NavigatesToWizardWithTitle()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LandingHero>(1);
            builder.AddAttribute(2, nameof(LandingHero.QuestionText), "addams");
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<LandingHero>();

        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        await cut.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(
            new MachineSuggestion("opdb-bally-addams-family", "The Addams Family", "Bally", 1992)));

        Assert.EndsWith("/wizard?q=The%20Addams%20Family", navMan.Uri, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8b. REGRESSION (question hijack): a QUESTION that surfaces a suggestion
    //     must be submitted verbatim — never replaced by the machine title.
    //
    // MudAutocomplete 9.5.0 auto-highlights the first suggestion when the menu
    // opens and Enter always selects it (verified against MudAutocomplete
    // .razor.cs OnEnterKeyAsync — no property disables this).  So typing
    // "whats a good tournament strategy for stranger things" and pressing Enter
    // fires ValueChanged with the "Stranger Things" suggestion.  Before the fix
    // this discarded the question and navigated to /wizard?q=Stranger%20Things,
    // making the Wizard answer the wrong thing (observed live: "…godzilla" →
    // Mars God of War).  The title is NOT a prefix of the question, so the
    // intent test routes it back through the free-text path: ?q= must equal the
    // text the user actually asked.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LandingHero_SuggestionAutoSelectedOnQuestion_SubmitsQuestionNotTitle()
    {
        await using var ctx = new BunitContext();
        RegisterHeroServices(ctx);

        const string question = "whats a good tournament strategy for stranger things";

        string? submitted = null;
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LandingHero>(1);
            builder.AddAttribute(2, nameof(LandingHero.QuestionText), question);
            builder.AddAttribute(3, nameof(LandingHero.QuestionSubmitted),
                EventCallback.Factory.Create<string>(this, q => submitted = q));
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<LandingHero>();

        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        // Simulate MudAutocomplete auto-selecting its highlighted first
        // suggestion (a machine whose title appears in the question). This is
        // exactly what Enter does when the menu is open with results.
        var autocomplete = cut.FindComponent<MudAutocomplete<MachineSuggestion>>();
        await cut.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(
            new MachineSuggestion("opdb-stern-stranger-things", "Stranger Things", "Stern", 2019)));

        // The literal question flows through the free-text submit path...
        Assert.Equal(question, submitted);
        // ...and the machine title is NOT used as the query.
        Assert.DoesNotContain("q=Stranger%20Things", navMan.Uri, StringComparison.Ordinal);
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
