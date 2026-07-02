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
// AppDataGrid pager needs a live circuit). It loads
// IIngestionSourceRepository.StreamAllAsync in OnInitializedAsync; bUnit runs
// that synchronously, so WaitForAssertion sees the final state. Tests assert the
// real load path: rows render, the empty-state still fires on no sources, and a
// throwing repo surfaces the visible error state (Invariant #17), not a silent
// empty grid.
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
    public void SubFeeds_GroupUnderTheirManufacturer()
    {
        RegisterSources(ct => Stream([
            MakeSource("jjp", true, sourceGroup: "Jersey Jack Pinball"),
            MakeSource("jjp_bulletins", false, sourceGroup: "Jersey Jack Pinball",
                discoveryStatus: "NoSource", discoveryNotes: "n/a", discoveryDate: new DateOnly(2026, 5, 26)),
        ], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
            // One group header for the shared manufacturer, rendered once.
            Assert.Single(cut.FindAll("[data-testid='source-group-header']")));
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
}
