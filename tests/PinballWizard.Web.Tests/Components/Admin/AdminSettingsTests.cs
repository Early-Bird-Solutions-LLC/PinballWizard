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
    private readonly IAgentPromptOverrideRepository _promptRepo = Substitute.For<IAgentPromptOverrideRepository>();

    public AdminSettingsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdminSettingRecord>>([]));
        Services.AddSingleton(_repo);
        Services.AddSingleton(Options.Create(new AiFoundryOptions()));

        // Prompt Templates tab dependencies (PR-B3). The embedded provider
        // is the real one — parameterless, reads the Application assembly's
        // .md resources; using it real also pins that the resources exist.
        _promptRepo.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(null));
        _promptRepo.GetVersionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentPromptOverride>>([]));
        Services.AddSingleton(_promptRepo);
        Services.AddSingleton(new PinballWizard.Application.Ai.EmbeddedResourceAgentPromptProvider());

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    // MudSelect (prompt tab) needs MudBlazor's popover infrastructure in
    // the same renderer; the provider takes no ChildContent, so it renders
    // as a SIBLING fragment rather than a render-tree wrapper.
    private IRenderedComponent<AdminSettings> RenderPage()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminSettings>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminSettings>();
    }

    [Fact]
    public async Task Renders_AllFourTabs_WithLiveAndPlaceholderSurfaces()
    {
        var cut = RenderPage();

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

        var cut = RenderPage();

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
        var cut = RenderPage();
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

        var cut = RenderPage();

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

    // ── Prompt Templates tab (PR-B3) ──────────────────────────────────────

    // MudTabs renders panels lazily — the prompt tab's content exists only
    // after its header is activated. Shared arrange step for the tab tests.
    private static async Task OpenPromptTabAndSelectAgentAsync(IRenderedComponent<AdminSettings> cut, string agent)
    {
        var header = cut.FindAll(".mud-tab").First(e => e.TextContent.Contains("Prompt Templates"));
        await header.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-agent-select']"));

        await cut.InvokeAsync(async () =>
        {
            var select = cut.FindComponents<MudBlazor.MudSelect<string>>()
                .First(c => c.Instance.Label == "Agent");
            await select.Instance.ValueChanged.InvokeAsync(agent);
        });
    }


    [Fact]
    public async Task PromptTab_SelectingAgent_LoadsDefaultIntoEditor_AndShowsStatus()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='settings-tabs']"));

        await OpenPromptTabAndSelectAgentAsync(cut, "Wizard");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Using the embedded default", cut.Find("[data-testid='prompt-status']").TextContent);
            var editor = cut.Find("textarea[data-testid='prompt-editor'], [data-testid='prompt-editor']");
            Assert.False(string.IsNullOrWhiteSpace(editor.TextContent + editor.GetAttribute("value")),
                "Editor should preload the embedded default prompt.");
        });
    }

    [Fact]
    public async Task PromptTab_SaveNewVersion_SavesInactive_WithResolvedUpdatedBy()
    {
        _promptRepo.SaveNewVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new AgentPromptOverride(
                ci.ArgAt<string>(0), 1, ci.ArgAt<string>(1), false,
                DateTimeOffset.UnixEpoch, ci.ArgAt<string>(2))));

        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='settings-tabs']"));
        await OpenPromptTabAndSelectAgentAsync(cut, "Repair");
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-save-button']"));

        await cut.Find("[data-testid='prompt-save-button']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            _promptRepo.Received(1).SaveNewVersionAsync(
                "Repair",
                Arg.Is<string>(c => !string.IsNullOrWhiteSpace(c)),
                Arg.Is<string>(u => u.Contains("admin") || u.Contains("test-admin")),
                Arg.Any<CancellationToken>());
            // Saving must NOT activate — the two-step contract.
            _promptRepo.DidNotReceiveWithAnyArgs().ActivateAsync(default!, default, default);
        });
    }

    [Fact]
    public async Task PromptTab_ActiveOverride_ShowsProvenance_AndVersionList()
    {
        var active = new AgentPromptOverride("Wizard", 2, "custom prompt", true,
            DateTimeOffset.Parse("2026-06-12T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "jim");
        var v1 = new AgentPromptOverride("Wizard", 1, "older", false,
            DateTimeOffset.Parse("2026-06-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "jim");
        _promptRepo.GetActiveAsync("Wizard", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentPromptOverride?>(active));
        _promptRepo.GetVersionsAsync("Wizard", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentPromptOverride>>([v1, active]));

        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.Find("[data-testid='settings-tabs']"));
        await OpenPromptTabAndSelectAgentAsync(cut, "Wizard");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Override v2 active — jim", cut.Find("[data-testid='prompt-status']").TextContent);
            cut.Find("[data-testid='prompt-active-chip-2']");
            cut.Find("[data-testid='prompt-activate-1']"); // inactive version offers Activate
        });
    }
}
