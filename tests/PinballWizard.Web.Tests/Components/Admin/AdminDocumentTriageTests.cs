using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminDocumentTriage.razor (/admin/document-triage).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminDocumentTriage is behind the global auth FallbackPolicy;
// tests run with AddAuthorization() set to authenticated.
//
// The repository mock returns an empty async sequence so the component
// completes OnAfterRenderAsync immediately and the empty-state path fires.
// Tests assert structural invariants: grid sentinel, empty-state message,
// and breadcrumb trail.
public sealed class AdminDocumentTriageTests : AsyncBunitContext
{
    private static async IAsyncEnumerable<RawDocumentRecord> EmptyRawDocuments(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield break;
    }

    public AdminDocumentTriageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddScoped<AdminActionGuard>();

        // Register mocks BEFORE GetRequiredService — bUnit locks the service
        // provider on the first GetService call (including BunitNavigationManager).
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo
            .StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => EmptyRawDocuments(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(rawRepo);
        Services.AddSingleton(Substitute.For<IDocumentLinker>());

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminDocumentTriage_Renders_WithoutThrowing()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminDocumentTriage_Renders_DataGridSentinel()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var grid = cut.Find("[data-testid='admin-document-triage-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public async Task AdminDocumentTriage_EmptyRepository_RendersEmptyStateMessage()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Behavioral assertion: empty async sequence causes the <NoRecordsContent>
        // empty-state to render with the expected message.
        var empty = cut.Find("[data-testid='admin-document-triage-empty']");
        Assert.Contains("No documents awaiting triage", empty.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminDocumentTriage_Breadcrumb_ContainsAdminRoot()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }

}

// Behavioral test: page shell + spinner render BEFORE data arrives; spinner hides
// AFTER. This is the instant-navigation contract (fix/admin-nav-instant-load).
public sealed class AdminDocumentTriageLoadingStateTests : AsyncBunitContext
{
    private readonly TaskCompletionSource _dataGate = new();

    private async IAsyncEnumerable<RawDocumentRecord> SlowStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _dataGate.Task.WaitAsync(ct);
        yield break;
    }

    public AdminDocumentTriageLoadingStateTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddScoped<AdminActionGuard>();

        var slowRepo = Substitute.For<IRawDocumentRepository>();
        slowRepo
            .StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => SlowStream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(slowRepo);
        Services.AddSingleton(Substitute.For<IDocumentLinker>());

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminDocumentTriage_ShowsSpinner_BeforeDataArrives()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();

        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        _dataGate.SetResult();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdminDocumentTriage_HidesSpinner_AfterDataArrives()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();

        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        _dataGate.SetResult();
        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal));

        await Task.CompletedTask;
    }
}

// Separate context for the Cosmos load-failure path.
// The repo throws so the page must show the distinct error alert and must NOT
// show the "No documents awaiting triage" empty-state (which implies data, not failure).
public sealed class AdminDocumentTriageLoadFailureTests : AsyncBunitContext
{
    public AdminDocumentTriageLoadFailureTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddScoped<AdminActionGuard>();

        var failRepo = Substitute.For<IRawDocumentRepository>();
        failRepo
            .StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<RawDocumentRecord>>(_ =>
                throw new InvalidOperationException("Cosmos unavailable"));
        Services.AddSingleton(failRepo);
        Services.AddSingleton(Substitute.For<IDocumentLinker>());

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminDocumentTriage_LoadFails_RendersErrorAlert()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Must render the distinct load-failed sentinel.
        cut.Find("[data-testid='triage-load-failed']");
    }

    [Fact]
    public async Task AdminDocumentTriage_LoadFails_DoesNotRenderEmptyStateText()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The misleading "No documents awaiting triage" text must be absent — a load
        // failure is not an empty queue and must not tell admins that all docs are resolved.
        Assert.Empty(cut.FindAll("[data-testid='admin-document-triage-empty']"));
    }
}

// Authorized one-row render tests — verifies that action buttons appear for admins.
public sealed class AdminDocumentTriageAuthorizedActionTests : AsyncBunitContext
{
    private static async IAsyncEnumerable<RawDocumentRecord> OneTriageRowAuthorized(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return new RawDocumentRecord
        {
            DocumentId = "doc_triage_1",
            DocumentUrl = "https://example.com/doc.pdf",
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/triage-source",
                DiscoveryContext = "Test",
                FileUrl = "https://example.com/doc.pdf",
                LinkText = "Test Manual",
                ActionType = ActionType.OpenPdf,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            LinkStatus = LinkStatus.Failed,
        };
    }

    public AdminDocumentTriageAuthorizedActionTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization()
            .SetAuthorized("test-admin@example.com")
            .SetPolicies("AdminOnly");
        Services.AddScoped<AdminActionGuard>();

        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo
            .StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => OneTriageRowAuthorized(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(rawRepo);
        Services.AddSingleton(Substitute.For<IDocumentLinker>());

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task Authorized_RendersActionButtons()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotEmpty(cut.FindAll("[data-testid='triage-action-relink']"));
    }
}

// Anonymous render tests — page is publicly readable after the admin showcase split.
// Verifies that queue content renders for unauthenticated visitors while gated
// action buttons (Relink / MarkGeneric) are hidden.
public sealed class AdminDocumentTriageAnonymousTests : AsyncBunitContext
{
    private static async IAsyncEnumerable<RawDocumentRecord> OneTriageRow(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return new RawDocumentRecord
        {
            DocumentId = "doc_triage_1",
            DocumentUrl = "https://example.com/doc.pdf",
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/triage-source",
                DiscoveryContext = "Test",
                FileUrl = "https://example.com/doc.pdf",
                LinkText = "Test Manual",
                ActionType = ActionType.OpenPdf,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            LinkStatus = LinkStatus.Failed,
        };
    }

    public AdminDocumentTriageAnonymousTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization(); // NOT authorized → _isAdmin false
        Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();

        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo
            .StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => OneTriageRow(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(rawRepo);
        Services.AddSingleton(Substitute.For<IDocumentLinker>());

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void Anonymous_ShowsQueue_HidesActions()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        cut.WaitForAssertion(() =>
        {
            // Read content present (a queue row's document id).
            Assert.Contains("doc_triage_1", cut.Markup, StringComparison.Ordinal);
            // Gated actions absent.
            Assert.Empty(cut.FindAll("[data-testid='triage-action-relink']"));
            Assert.Empty(cut.FindAll("[data-testid='triage-action-markgeneric']"));
        });
    }
}
