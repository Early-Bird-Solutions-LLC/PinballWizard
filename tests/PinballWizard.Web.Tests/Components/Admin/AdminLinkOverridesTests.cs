using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminLinkOverrides.razor (/admin/link-overrides).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminLinkOverrides is behind the global auth FallbackPolicy;
// tests run with AddAuthorization() set to authenticated.
//
// The repository mock returns an empty dictionary so the component completes
// OnInitializedAsync immediately and the empty-state path fires. Tests assert
// structural invariants: grid sentinel, empty-state message, New Override button,
// and breadcrumb trail.
public sealed class AdminLinkOverridesTests : AsyncBunitContext
{
    public AdminLinkOverridesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        // Register mocks BEFORE GetRequiredService — bUnit locks the service
        // provider on the first GetService call (including BunitNavigationManager).
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        overrideRepo
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, LinkOverrideRecord>>(
                new Dictionary<string, LinkOverrideRecord>()));
        Services.AddSingleton(overrideRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminLinkOverrides_Renders_WithoutThrowing()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminLinkOverrides_Renders_DataGridSentinel()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var grid = cut.Find("[data-testid='admin-link-overrides-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public async Task AdminLinkOverrides_EmptyRepository_RendersEmptyStateMessage()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Behavioral assertion: empty dictionary from LoadAllAsync causes the
        // <NoRecordsContent> empty-state to render with the expected message.
        var empty = cut.Find("[data-testid='admin-link-overrides-empty']");
        Assert.Contains("No overrides configured", empty.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminLinkOverrides_Renders_NewOverrideButton()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The "New Override" button must be present and accessible.
        var buttons = cut.FindAll("button");
        Assert.Contains(buttons, b => b.TextContent.Contains("New Override", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminLinkOverrides_Breadcrumb_ContainsAdminRoot()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }
}

// Separate context for the Cosmos load-failure path.
// The repo throws so the page must show the distinct error alert and must NOT
// show the "No overrides configured" empty-state (which implies data, not failure).
public sealed class AdminLinkOverridesLoadFailureTests : AsyncBunitContext
{
    public AdminLinkOverridesLoadFailureTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var failRepo = Substitute.For<ILinkOverrideRepository>();
        failRepo
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, LinkOverrideRecord>>>(_ =>
                throw new InvalidOperationException("Cosmos unavailable"));
        Services.AddSingleton(failRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminLinkOverrides_LoadFails_RendersErrorAlert()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Must render the distinct load-failed sentinel.
        cut.Find("[data-testid='overrides-load-failed']");
    }

    [Fact]
    public async Task AdminLinkOverrides_LoadFails_DoesNotRenderEmptyStateText()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The misleading "No overrides configured" text must be absent — a load failure
        // is not an empty override set and must not tell admins there is nothing to manage.
        Assert.Empty(cut.FindAll("[data-testid='admin-link-overrides-empty']"));
    }
}
