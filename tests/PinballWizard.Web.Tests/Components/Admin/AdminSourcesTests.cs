using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminSources.razor (/admin/sources).
//
// AdminSources is @rendermode InteractiveServer (ADR-0034 amendment: its
// AppDataGrid pager needs a live circuit) and inherits AdminPageBase, loading
// IIngestionSourceRepository.StreamAllAsync in OnAfterRenderAsync (spinner-before-
// data, mirroring AdminMachines). bUnit fires the after-render load and pumps the
// dispatcher, so WaitForAssertion sees the final state. Tests assert the real load
// path: rows render, the empty-state still fires on no sources, and a throwing repo
// surfaces the visible error state (Invariant #17), not a silent empty grid. The
// AdminSourcesLoadingStateTests context below locks the spinner-before-data contract.
public sealed class AdminSourcesTests : AsyncBunitContext
{
    private static IngestionSource MakeSource(
        string id, bool enabled,
        string? sourceGroup = null,
        string? discoveryStatus = null,
        string? discoveryNotes = null,
        DateOnly? discoveryDate = null) => new()
    {
        Id = id,
        DisplayName = $"{id} Pinball",
        ScraperImplKey = id,
        BaseUrl = $"https://{id}.example.com",
        Enabled = enabled,
        Cadence = "weekly",
        SourceGroup = sourceGroup ?? $"{id} Group",
        DiscoveryStatus = discoveryStatus,
        DiscoveryNotes = discoveryNotes,
        DiscoveryDate = discoveryDate,
        TotalDocumentsDiscovered = 7,
        TotalRunFailures = 0,
    };

    private static async IAsyncEnumerable<IngestionSource> Stream(
        IEnumerable<IngestionSource> items,
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }

    private static async IAsyncEnumerable<IngestionSource> ThrowingStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162 // unreachable — required to make this a valid iterator
        yield break;
#pragma warning restore CS0162
    }

    private void RegisterSources(Func<CancellationToken, IAsyncEnumerable<IngestionSource>> stream)
    {
        var repo = Substitute.For<IIngestionSourceRepository>();
        repo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => stream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(repo);
    }

    public AdminSourcesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
    }

    [Fact]
    public void WithSources_RendersStatusVocabulary()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true), MakeSource("jjp", false)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("stern Pinball", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Active", cut.Markup, StringComparison.Ordinal);   // enabled row
            Assert.Contains("Disabled", cut.Markup, StringComparison.Ordinal); // disabled, no discovery reason
        });
    }

    [Fact]
    public void NoSourceRow_RendersNoSourceChipAndInlineReason()
    {
        RegisterSources(ct => Stream([
            MakeSource("jjp_bulletins", false,
                sourceGroup: "Jersey Jack Pinball",
                discoveryStatus: "NoSource",
                discoveryNotes: "No bulletin section exists here.",
                discoveryDate: new DateOnly(2026, 5, 26))
        ], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No source", cut.Markup, StringComparison.Ordinal);
            var reason = cut.Find("[data-testid='source-reason']");
            Assert.Contains("No bulletin section exists here.", reason.TextContent, StringComparison.Ordinal);
            Assert.Contains("2026-05-26", reason.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NoSourceRow_WithoutDate_RendersReasonWithoutAssessedSuffix()
    {
        RegisterSources(ct => Stream([
            MakeSource("jjp_bulletins", false,
                sourceGroup: "Jersey Jack Pinball",
                discoveryStatus: "NoSource",
                discoveryNotes: "No bulletin section exists here.")
                // discoveryDate intentionally left null
        ], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            var reason = cut.Find("[data-testid='source-reason']");
            Assert.Contains("No bulletin section exists here.", reason.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("assessed", reason.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ActiveRow_RendersNoReasonCaption()
    {
        RegisterSources(ct => Stream([
            MakeSource("stern", true, sourceGroup: "Stern Pinball",
                discoveryStatus: "Active", discoveryNotes: "Should not be shown for active.")
        ], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
            Assert.Empty(cut.FindAll("[data-testid='source-reason']")));
    }

    [Fact]
    public void EmptyList_RendersNoSourcesConfiguredMessage()
    {
        RegisterSources(ct => Stream([], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find("[data-testid='admin-sources-empty']");
            Assert.Contains("No sources configured", empty.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void LoadFailure_RendersVisibleErrorState()
    {
        RegisterSources(ThrowingStream);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='admin-sources-load-failed']");
            // Failure must NOT masquerade as the benign empty-state.
            Assert.Empty(cut.FindAll("[data-testid='admin-sources-empty']"));
        });
    }

    [Fact]
    public void Breadcrumb_ContainsAdminRoot()
    {
        RegisterSources(ct => Stream([], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() => cut.Find("a[href='/admin']"));
    }

    [Fact]
    public void SourceName_LinksToDetailPage()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
            cut.Find("a[href='/admin/sources/stern']"));
    }

    [Fact]
    public void SourceUrl_RendersAsLink()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
            cut.Find("a[href='https://stern.example.com']"));
    }

    [Fact]
    public void WithSources_RendersGridSearchBox()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='grid-search-input']"));
    }
}

// Behavioral parity with AdminMachinesLoadingStateTests: the page shell + spinner
// render BEFORE data arrives, and the spinner clears AFTER. This is the
// instant-navigation contract that OnAfterRenderAsync + AdminPageBase.SafeStateHasChanged
// provide — the alignment issue #635 brings /admin/sources to match /admin/machines.
//
// Pattern: hold the repository call with a TaskCompletionSource so we can assert the
// loading state between render and data arrival. OnAfterRenderAsync kicks LoadAsync off
// after the first render, so the spinner is present immediately after that render.
// NOTE: bUnit cannot distinguish the two lifecycles — the spinner also shows before the
// gate under OnInitializedAsync; OnAfterRenderAsync's real benefit is not blocking the
// SSR pre-render pass, which is not bUnit-observable. These are parity/regression guards
// (matching AdminMachines' coverage), not a lifecycle RED.
public sealed class AdminSourcesLoadingStateTests : AsyncBunitContext
{
    private readonly TaskCompletionSource _dataGate = new();

    private async IAsyncEnumerable<IngestionSource> SlowStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Hold until the test releases the gate — simulates a slow Cosmos query.
        await _dataGate.Task.WaitAsync(ct);
        yield break;
    }

    public AdminSourcesLoadingStateTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var slowRepo = Substitute.For<IIngestionSourceRepository>();
        slowRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => SlowStream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(slowRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminSources_ShowsSpinner_BeforeDataArrives()
    {
        // Render without releasing the slow data — the loading bar must be present
        // immediately after the first render, before the gate is released.
        var cut = RenderWithPopover<AdminSources>();

        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        // Release so teardown doesn't hang.
        _dataGate.SetResult();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdminSources_HidesSpinner_AfterDataArrives()
    {
        var cut = RenderWithPopover<AdminSources>();

        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        // Release the gate, then drain the renderer dispatcher deterministically:
        // SetResult completes the IAsyncEnumerable's WaitAsync, which posts the stream's
        // MoveNextAsync continuation onto the dispatcher; that continuation's await foreach
        // exit posts a second continuation (LoadAsync's finally: _loading = false +
        // SafeStateHasChanged). Two InvokeAsync flushes run both, so the assertion never
        // races thread-pool scheduling in the common case. WaitForAssertion's wall-clock
        // poll was the flake under CI load (#898) — same root cause as #822, fixed the
        // same way in 4a25752.
        //
        // The determinism argument depends on every await in that chain staying on the
        // renderer's dispatcher (neither ConfigureAwait(false) nor a
        // RunContinuationsAsynchronously TaskCompletionSource appears anywhere in it —
        // verified against LoadAsync/SlowStream/_dataGate above; both would break the
        // capture-and-resume-on-dispatcher assumption this relies on). A short
        // WaitForAssertion after the flushes is a bounded safety net, not the primary
        // mechanism: it should resolve immediately once the two flushes have run, and
        // only pays its timeout if that assumption is ever violated by a future change.
        _dataGate.SetResult();
        await cut.InvokeAsync(() => Task.CompletedTask);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(
            () => Assert.DoesNotContain("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(1));
    }
}
