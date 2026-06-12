using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminSettings.razor (/admin/settings, PR-B2).
//
// Behaviors under test (not structure): the layering display (override
// provenance vs default), the save path writing only dirty+valid rows with
// the resolved updatedBy, and the honest load-error state (the page must
// say the Wizard is unaffected, not render dead controls).
public sealed class AdminSettingsTests : AsyncBunitContext
{
    private readonly IAdminSettingsRepository _repo = Substitute.For<IAdminSettingsRepository>();

    public AdminSettingsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdminSettingRecord>>([]));
        Services.AddSingleton(_repo);
        Services.AddSingleton(Options.Create(new AiFoundryOptions()));

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task Renders_AllFourTabs_WithLiveAndPlaceholderSurfaces()
    {
        var cut = Render<AdminSettings>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='settings-tabs']");
            cut.Find("[data-testid='setting-confidence']");
        });

        // The RAG tab is live (retrieval keys gained call-time consumers);
        // Prompt Templates remains an honest placeholder until Phase 3.
        // (Panels render lazily; assert the tab headers are present.)
        Assert.Contains("RAG Retrieval", cut.Markup);
        Assert.Contains("Prompt Templates", cut.Markup);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task StoredOverride_ShowsProvenance_AndDefaultStaysHinted()
    {
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdminSettingRecord>>(
            [
                new("ai.confidence_threshold", "0.8", DateTimeOffset.Parse("2026-06-12T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "jim"),
            ]));

        var cut = Render<AdminSettings>();

        cut.WaitForAssertion(() =>
        {
            var provenance = cut.Find("[data-testid='provenance-ai.confidence_threshold']");
            Assert.Contains("jim", provenance.TextContent);
            Assert.Contains("2026-06-12", provenance.TextContent);
        });

        // The effective slider value is the override, the hint names the default.
        Assert.Contains("0.80", cut.Find("[data-testid='confidence-value']").TextContent);
        Assert.Contains("Default: 0.65", cut.Markup);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Save_WritesOnlyDirtyRows_WithResolvedUpdatedBy()
    {
        var cut = Render<AdminSettings>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='ceiling-input']"));

        // Dirty the ceiling via its numeric input; leave the others pristine.
        // MudBaseInput splats UserAttributes onto the <input> element itself,
        // so the testid IS the input — no descendant selector.
        var input = cut.Find("input[data-testid='ceiling-input']");
        await cut.InvokeAsync(() => input.Change("25"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='dirty-hint']"));
        await cut.Find("[data-testid='save-button']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            _repo.Received(1).SetAsync(
                "ai.per_call_cost_ceiling_usd_cents",
                "25",
                Arg.Is<string>(s => s.Contains("test-admin") || s.Contains("admin")),
                Arg.Any<CancellationToken>());
        });

        // Pristine keys were NOT written — saving must never touch
        // settings the admin didn't change.
        await _repo.DidNotReceive().SetAsync(
            "ai.confidence_threshold", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadFailure_RendersHonestErrorState_NoDeadControls()
    {
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AdminSettingRecord>>>(_ => throw new InvalidOperationException("cosmos down"));

        var cut = Render<AdminSettings>();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid='settings-load-error']");
            Assert.Contains("cosmos down", alert.TextContent);
            // The honest part: the Wizard itself is unaffected and the page says so.
            Assert.Contains("still running", alert.TextContent);
        });

        // No editable surface renders against unknown state.
        Assert.Empty(cut.FindAll("[data-testid='settings-tabs']"));
        Assert.Empty(cut.FindAll("[data-testid='save-button']"));

        await Task.CompletedTask;
    }
}
