using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminDocumentTriage.razor (/admin/document-triage).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminDocumentTriage is behind the global auth FallbackPolicy;
// tests run with AddAuthorization() set to authenticated.
//
// The repository mock returns an empty async sequence so the component
// completes OnInitializedAsync immediately and the empty-state path fires.
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
        var cut = Render<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminDocumentTriage_Renders_DataGridSentinel()
    {
        var cut = Render<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var grid = cut.Find("[data-testid='admin-document-triage-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public async Task AdminDocumentTriage_EmptyRepository_RendersEmptyStateMessage()
    {
        var cut = Render<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Behavioral assertion: empty async sequence causes the <NoRecordsContent>
        // empty-state to render with the expected message.
        var empty = cut.Find("[data-testid='admin-document-triage-empty']");
        Assert.Contains("No documents awaiting triage", empty.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminDocumentTriage_Breadcrumb_ContainsAdminRoot()
    {
        var cut = Render<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
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
        var cut = Render<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Must render the distinct load-failed sentinel.
        cut.Find("[data-testid='triage-load-failed']");
    }

    [Fact]
    public async Task AdminDocumentTriage_LoadFails_DoesNotRenderEmptyStateText()
    {
        var cut = Render<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The misleading "No documents awaiting triage" text must be absent — a load
        // failure is not an empty queue and must not tell admins that all docs are resolved.
        Assert.Empty(cut.FindAll("[data-testid='admin-document-triage-empty']"));
    }
}
