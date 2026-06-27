using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminLinkOverrides.razor (/admin/link-overrides).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminLinkOverrides is public-read after the admin showcase
// split; tests run with AddAuthorization() set to authenticated + AdminOnly policy.
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
        this.AddAuthorization()
            .SetAuthorized("test-admin@example.com")
            .SetPolicies(AuthorizationPolicies.AdminOnly);
        Services.AddScoped<AdminActionGuard>();

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

        // The "New Override" button must be present and accessible for admins.
        var buttons = cut.FindAll("[data-testid='overrides-new-button']");
        Assert.NotEmpty(buttons);
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

// Behavioral test: page shell + spinner render BEFORE data arrives; spinner hides
// AFTER. This is the instant-navigation contract (fix/admin-nav-instant-load).
public sealed class AdminLinkOverridesLoadingStateTests : AsyncBunitContext
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, LinkOverrideRecord>> _dataGate = new();

    public AdminLinkOverridesLoadingStateTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization()
            .SetAuthorized("test-admin@example.com")
            .SetPolicies(AuthorizationPolicies.AdminOnly);
        Services.AddScoped<AdminActionGuard>();

        var slowRepo = Substitute.For<ILinkOverrideRepository>();
        slowRepo
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _dataGate.Task);
        Services.AddSingleton(slowRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminLinkOverrides_ShowsSpinner_BeforeDataArrives()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();

        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        _dataGate.SetResult(new Dictionary<string, LinkOverrideRecord>());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdminLinkOverrides_HidesSpinner_AfterDataArrives()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();

        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        _dataGate.SetResult(new Dictionary<string, LinkOverrideRecord>());
        await cut.InvokeAsync(() => Task.CompletedTask);
        await cut.InvokeAsync(() => Task.CompletedTask);
        Assert.DoesNotContain("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);
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
        this.AddAuthorization()
            .SetAuthorized("test-admin@example.com")
            .SetPolicies(AuthorizationPolicies.AdminOnly);
        Services.AddScoped<AdminActionGuard>();

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

// Authorized one-row render tests — verifies admin controls appear for admins.
public sealed class AdminLinkOverridesAuthorizedActionTests : AsyncBunitContext
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public AdminLinkOverridesAuthorizedActionTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization()
            .SetAuthorized("test-admin@example.com")
            .SetPolicies(AuthorizationPolicies.AdminOnly);
        Services.AddScoped<AdminActionGuard>();

        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var seed = new LinkOverrideRecord
        {
            SourcePattern = "sternpinball.com/x",
            MachineIds = ["mch_godzilla_pro"],
            CreatedBy = "admin (local-dev)",
            CreatedAt = AsOf,
            Notes = "seed override",
        };
        overrideRepo
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, LinkOverrideRecord>>(
                new Dictionary<string, LinkOverrideRecord> { [seed.SourcePattern] = seed }));
        Services.AddSingleton(overrideRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void Authorized_RendersNewButtonAndDeleteAndCreatedBy()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid='overrides-new-button']"));
            Assert.NotEmpty(cut.FindAll("[data-testid='overrides-delete']"));
            Assert.Contains("Created By", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("admin (local-dev)", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("seed override", cut.Markup, StringComparison.Ordinal);
        });
    }
}

// Anonymous render tests — page is publicly readable after the admin showcase split.
// Verifies that override rows render for unauthenticated visitors while gated
// action buttons (New Override / Delete) and identity columns (Created By) are hidden.
public sealed class AdminLinkOverridesAnonymousTests : AsyncBunitContext
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public AdminLinkOverridesAnonymousTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization(); // NOT authorized → _isAdmin false
        Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();

        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var seed = new LinkOverrideRecord
        {
            SourcePattern = "sternpinball.com/x",
            MachineIds = ["mch_godzilla_pro"],
            CreatedBy = "admin (local-dev)",
            CreatedAt = AsOf,
            Notes = "seed override",
        };
        overrideRepo
            .LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, LinkOverrideRecord>>(
                new Dictionary<string, LinkOverrideRecord> { [seed.SourcePattern] = seed }));
        Services.AddSingleton(overrideRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void Anonymous_ShowsRows_HidesActionsAndIdentity()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        cut.WaitForAssertion(() =>
        {
            // override data present (the seeded source pattern)
            Assert.Contains("sternpinball.com/x", cut.Markup, StringComparison.Ordinal);
            // gated: no New Override button, no Delete, no identity columns
            Assert.Empty(cut.FindAll("[data-testid='overrides-new-button']"));
            Assert.Empty(cut.FindAll("[data-testid='overrides-delete']"));
            Assert.DoesNotContain("Created By", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("admin (local-dev)", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("seed override", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Notes", cut.Markup, StringComparison.Ordinal);
        });
    }
}
