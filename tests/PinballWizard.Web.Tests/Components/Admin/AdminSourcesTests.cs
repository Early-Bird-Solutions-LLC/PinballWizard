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
    private static IngestionSource MakeSource(string id, bool enabled) => new()
    {
        Id = id,
        DisplayName = $"{id} Pinball",
        ScraperImplKey = id,
        BaseUrl = $"https://{id}.example.com",
        Enabled = enabled,
        Cadence = "weekly",
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
    public void WithSources_RendersRows()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true), MakeSource("jjp", false)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("stern Pinball", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("jjp Pinball", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Enabled", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Disabled", cut.Markup, StringComparison.Ordinal);
        });
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
