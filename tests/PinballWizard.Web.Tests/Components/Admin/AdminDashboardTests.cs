using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminDashboard.razor (/admin).
//
// Static SSR + [StreamRendering] (ADR-0034). The four cards load from three
// repositories in OnInitializedAsync. Tests assert the real counts render
// (Machines/Documents from catalog_stats, Sources, Link Overrides) and that a
// throwing repo surfaces the per-card error sentinel (Invariant #17) rather
// than a silent dash.
public sealed class AdminDashboardTests : AsyncBunitContext
{
    private static readonly DateTimeOffset AsOf =
        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // stern: 1 machine / 0 docs ; jjp: 2 machines / (2 + 1) = 3 docs.
    // Totals: 3 machines, 3 documents.
    private static readonly ManufacturerCatalogStats Stern = new(
        "stern", AsOf,
        [new MachineDocStats("mch_a", "Foo", "Pro", "foo", 2024, false, 0,
            new Dictionary<string, int>(), false)]);

    private static readonly ManufacturerCatalogStats Jjp = new(
        "jjp", AsOf,
        [
            new MachineDocStats("mch_b", "Bar CE", "CE", "bar", 2023, false, 2,
                new Dictionary<string, int> { ["Manual"] = 1 }, true),
            new MachineDocStats("mch_c", "Bar LE", "LE", "bar", 2023, false, 1,
                new Dictionary<string, int> { ["Manual"] = 1 }, true),
        ]);

    private static async IAsyncEnumerable<ManufacturerCatalogStats> StatsStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return Stern;
        yield return Jjp;
    }

    private static async IAsyncEnumerable<ManufacturerCatalogStats> ThrowingStatsStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IngestionSource> SourcesStream(
        int count, [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        for (var i = 0; i < count; i++)
            yield return new IngestionSource
            {
                Id = $"s{i}", DisplayName = $"Source {i}", ScraperImplKey = $"s{i}",
                BaseUrl = $"https://s{i}.example.com", Enabled = true, Cadence = "weekly",
            };
    }

    private static async IAsyncEnumerable<IngestionSource> ThrowingSourcesStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private void RegisterAll(
        Func<CancellationToken, IAsyncEnumerable<ManufacturerCatalogStats>> statsStream,
        int sourceCount = 2,
        int overrideCount = 1)
    {
        var stats = Substitute.For<ICatalogStatsReadRepository>();
        stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(ci => statsStream(ci.Arg<CancellationToken>()));
        Services.AddSingleton(stats);

        var sources = Substitute.For<IIngestionSourceRepository>();
        sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(ci => SourcesStream(sourceCount, ci.Arg<CancellationToken>()));
        Services.AddSingleton(sources);

        var overrides = Substitute.For<ILinkOverrideRepository>();
        var dict = new Dictionary<string, LinkOverrideRecord>();
        for (var i = 0; i < overrideCount; i++)
            dict[$"p{i}"] = new LinkOverrideRecord
            {
                SourcePattern = $"p{i}", MachineIds = [], CreatedBy = "test", CreatedAt = AsOf,
            };
        overrides.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, LinkOverrideRecord>)dict);
        Services.AddSingleton(overrides);
    }

    public AdminDashboardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
    }

    [Fact]
    public void RendersRealCounts()
    {
        RegisterAll(StatsStream, sourceCount: 2, overrideCount: 1);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("3", cut.Find("[data-testid='admin-machines-count']").TextContent.Trim());
            Assert.Equal("3", cut.Find("[data-testid='admin-documents-count']").TextContent.Trim());
            Assert.Equal("2", cut.Find("[data-testid='admin-sources-count']").TextContent.Trim());
            Assert.Equal("1", cut.Find("[data-testid='admin-link-overrides-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void StatsLoadFailure_RendersErrorSentinels_NotADash()
    {
        RegisterAll(ThrowingStatsStream, sourceCount: 2, overrideCount: 1);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Machines + Documents share the catalog_stats load → both error.
            cut.Find("[data-testid='admin-machines-count-error']");
            cut.Find("[data-testid='admin-documents-count-error']");
            Assert.Empty(cut.FindAll("[data-testid='admin-machines-count']"));
            // Independent loads are unaffected.
            Assert.Equal("2", cut.Find("[data-testid='admin-sources-count']").TextContent.Trim());
            // Overrides load is independent of the stats failure.
            Assert.Equal("1", cut.Find("[data-testid='admin-link-overrides-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void ViewCatalogButton_HrefsAdminMachines()
    {
        RegisterAll(StatsStream);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() => cut.Find("a[href='/admin/machines']"));
    }

    [Fact]
    public void SourcesLoadFailure_RendersSourcesErrorSentinel_OthersUnaffected()
    {
        // Sources stream throws; stats + overrides are on their happy fakes.
        var stats = Substitute.For<ICatalogStatsReadRepository>();
        stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(ci => StatsStream(ci.Arg<CancellationToken>()));
        Services.AddSingleton(stats);

        var sources = Substitute.For<IIngestionSourceRepository>();
        sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingSourcesStream(CancellationToken.None));
        Services.AddSingleton(sources);

        var overrides = Substitute.For<ILinkOverrideRepository>();
        overrides.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, LinkOverrideRecord>)new Dictionary<string, LinkOverrideRecord>
            {
                ["p0"] = new LinkOverrideRecord { SourcePattern = "p0", MachineIds = [], CreatedBy = "test", CreatedAt = AsOf },
            });
        Services.AddSingleton(overrides);

        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Sources card shows error sentinel.
            cut.Find("[data-testid='admin-sources-count-error']");
            Assert.Empty(cut.FindAll("[data-testid='admin-sources-count']"));
            // Independent loads (stats + overrides) are unaffected.
            Assert.Equal("3", cut.Find("[data-testid='admin-machines-count']").TextContent.Trim());
            Assert.Equal("1", cut.Find("[data-testid='admin-link-overrides-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void OverridesLoadFailure_RendersOverridesErrorSentinel_OthersUnaffected()
    {
        // Overrides LoadAllAsync throws; stats + sources are on their happy fakes.
        var stats = Substitute.For<ICatalogStatsReadRepository>();
        stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(ci => StatsStream(ci.Arg<CancellationToken>()));
        Services.AddSingleton(stats);

        var sources = Substitute.For<IIngestionSourceRepository>();
        sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(ci => SourcesStream(2, ci.Arg<CancellationToken>()));
        Services.AddSingleton(sources);

        var overrides = Substitute.For<ILinkOverrideRepository>();
        overrides.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, LinkOverrideRecord>>(
                new InvalidOperationException("simulated Cosmos failure")));
        Services.AddSingleton(overrides);

        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Link Overrides card shows error sentinel.
            cut.Find("[data-testid='admin-link-overrides-count-error']");
            Assert.Empty(cut.FindAll("[data-testid='admin-link-overrides-count']"));
            // Independent loads (stats + sources) are unaffected.
            Assert.Equal("3", cut.Find("[data-testid='admin-machines-count']").TextContent.Trim());
            Assert.Equal("2", cut.Find("[data-testid='admin-sources-count']").TextContent.Trim());
        });
    }
}
