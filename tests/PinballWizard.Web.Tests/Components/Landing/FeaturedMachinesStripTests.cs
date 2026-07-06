using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Landing;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit behavioral tests for FeaturedMachinesStrip.
//
// The regression these tests guard against: the card originally built the
// Wizard query from the internal slug (machine.MachineId, e.g. "jjp-wonka"),
// which getMachineByTitle cannot resolve — the live showcase answered
// "I could not find a direct match for 'JJP Wonka'". The query MUST be built
// from the human-readable, catalog-resolvable Title.
//
// Tests assert behavior (the NavigationManager URI after a click), not markup.
// Clicks are issued inside InvokeAsync and the element is located inside the
// same dispatcher pass to avoid the stale-handler-id flake documented in
// project_bunit_dispatcher_click_pattern.
public sealed class FeaturedMachinesStripTests
{
    private static FeaturedMachine[] BuildMachines() =>
    [
        new FeaturedMachine("jjp-wonka", "Wonka", null, null, 2,
            "Jersey Jack's whimsical masterpiece."),
        new FeaturedMachine("stern-godzilla-pro", "Godzilla Pro", null, null, 1,
            "The king of the monsters rules the playfield."),
    ];

    [Fact]
    public void FeaturedMachinesStrip_RendersOneCardPerMachine()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<FeaturedMachinesStrip>(p => p
            .Add(s => s.Machines, BuildMachines()));

        var cards = cut.FindAll("[data-testid^='featured-machine-']");
        Assert.Equal(2, cards.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // The core guard: clicking a card sends the Title as the Wizard query,
    // and NEVER the slug. "jjp-wonka" must not appear in the navigated URL;
    // "Wonka" must.
    // ──────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task FeaturedMachinesStrip_OnCardClick_NavigatesWithTitle_NotSlug()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        var cut = ctx.Render<FeaturedMachinesStrip>(p => p
            .Add(s => s.Machines, BuildMachines()));

        // The onclick handler lives on the inner MudCard (.featured-card), not
        // the outer cell div that carries the data-testid.
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='featured-machine-jjp-wonka'] .featured-card").Click());

        // The query is built from the Title ("Wonka"), URL-encoded. The internal
        // slug ("jjp-wonka") must never leak into the query a real user's Wizard
        // resolves against.
        Assert.Contains("/wizard?q=", navMan.Uri, StringComparison.Ordinal);
        Assert.Contains("Wonka", navMan.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("jjp-wonka", navMan.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeaturedMachinesStrip_OnCardClick_UsesTitleWithSpaces_ForMultiWordTitle()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        var cut = ctx.Render<FeaturedMachinesStrip>(p => p
            .Add(s => s.Machines, BuildMachines()));

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='featured-machine-stern-godzilla-pro'] .featured-card").Click());

        // "Godzilla Pro" resolves on the exact edition-lookup key; the slug
        // "stern-godzilla-pro" would not.
        Assert.Contains("Godzilla", navMan.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("stern-godzilla-pro", navMan.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void FeaturedMachinesStrip_RendersSkeletons_WhenMachinesIsNull()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<FeaturedMachinesStrip>(p => p
            .Add(s => s.Machines, (IReadOnlyList<FeaturedMachine>?)null));

        // Loading state renders the skeleton strip, no real cards.
        var loading = cut.FindAll("[data-testid='featured-machines-strip-loading']");
        Assert.Single(loading);
        Assert.Empty(cut.FindAll("[data-testid^='featured-machine-']"));
    }
}
