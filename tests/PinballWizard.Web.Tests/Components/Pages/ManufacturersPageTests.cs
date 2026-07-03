using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// bUnit tests for Manufacturers.razor (/manufacturers/{key}) — public catalog page.
// Games list is authoritative from IMachineRepository.StreamByManufacturerAsync
// (single-partition, works for every manufacturer). Per-machine doc counts are a
// left-join from the ICatalogStatsReadRepository rollup (join on MachineDocStats.
// MachineId == Machine.Id). Honest states (Invariant #17): machine-load-fail alert,
// not-found for an unknown key, and a stats-read failure that degrades doc counts to
// "—" while the games list still renders.
public sealed class ManufacturersPageTests : AsyncBunitContext
{
    private readonly IMachineRepository _machines = Substitute.For<IMachineRepository>();
    private readonly ICatalogStatsReadRepository _stats = Substitute.For<ICatalogStatsReadRepository>();

    public ManufacturersPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_machines);
        Services.AddSingleton(_stats);
        Services.AddSingleton<ILogger<Manufacturers>>(NullLogger<Manufacturers>.Instance);
    }

    private static Machine M(string key, string display, string id, string title, int? year, string? edition) => new()
    {
        Id = id, PartitionKey = key, ManufacturerDisplayName = display, Title = title,
        Year = year, EditionLabel = edition,
        FirstSeenAt = DateTimeOffset.MinValue, LastSeenAt = DateTimeOffset.MinValue,
    };

    private static MachineDocStats S(string id, string title, int docCount, bool hasManual) =>
        new(id, title, EditionLabel: null, GroupId: null, Year: null, IsOpdbOnly: false,
            DocCount: docCount, DocTypeCounts: new Dictionary<string, int>(), HasManual: hasManual);

    private static async IAsyncEnumerable<T> Stream<T>(params T[] items)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }

    private static async IAsyncEnumerable<T> Throwing<T>()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private IRenderedComponent<Manufacturers> RenderPage(string key)
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Manufacturers>(1);
            builder.AddAttribute(2, nameof(Manufacturers.Key), key);
            builder.CloseComponent();
        });
        return fragment.FindComponent<Manufacturers>();
    }

    [Fact]
    public async Task Populated_RendersGamesWithDocCountsAndBrowseAllLink()
    {
        _machines.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                M("stern", "Stern Pinball", "GRBN-MQR4P", "Stranger Things", 2019, "Pro"),
                M("stern", "Stern Pinball", "GXYZ-1", "Godzilla", 2021, null)));
        _stats.GetByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(new ManufacturerCatalogStats("stern", DateTimeOffset.MinValue,
                new[] { S("GRBN-MQR4P", "Stranger Things", 3, true), S("GXYZ-1", "Godzilla", 5, true) }));

        var cut = RenderPage("stern");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturer-games-table']");
        Assert.Contains("Stranger Things", table.TextContent, StringComparison.Ordinal);
        Assert.Contains("Godzilla", table.TextContent, StringComparison.Ordinal);
        // Grouped-by-machine doc counts present; total (8) surfaced via the browse-all link.
        var browse = cut.Find("[data-testid='manufacturer-browse-docs']");
        Assert.Contains("8", browse.TextContent, StringComparison.Ordinal);
        cut.Find("a[href='/documents?manufacturer=Stern%20Pinball']");
        // Alphabetical order (favoritism guardrail): Godzilla before Stranger Things.
        Assert.True(
            table.TextContent.IndexOf("Godzilla", StringComparison.Ordinal) <
            table.TextContent.IndexOf("Stranger Things", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpdbOnlyManufacturer_NoRollup_ShowsGamesZeroDocsNoBrowseLink()
    {
        // Williams has machines but no catalog_stats rollup (OPDB-only, no scraper).
        _machines.StreamByManufacturerAsync("williams", Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("williams", "Williams", "W-1", "Medieval Madness", 1997, null)));
        _stats.GetByManufacturerAsync("williams", Arg.Any<CancellationToken>())
            .Returns((ManufacturerCatalogStats?)null);

        var cut = RenderPage("williams");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturer-games-table']");
        Assert.Contains("Medieval Madness", table.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-browse-docs']"));
    }

    [Fact]
    public async Task UnknownKey_NoMachines_RendersNotFound()
    {
        _machines.StreamByManufacturerAsync("nope", Arg.Any<CancellationToken>())
            .Returns(_ => Stream<Machine>());

        var cut = RenderPage("nope");
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturer-not-found']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-games-table']"));
    }

    [Fact]
    public async Task MachineStreamFails_RendersVisibleAlertNoTable()
    {
        _machines.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(_ => Throwing<Machine>());

        var cut = RenderPage("stern");
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturer-load-failed']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-games-table']"));
    }

    [Fact]
    public async Task StatsReadFails_GamesStillRender_DocCountsDegradeToDash()
    {
        // Section-scoped: rollup read throws → games list from read 1 survives, doc
        // counts show "—", no browse-all link (Invariant #17 — no fabricated counts).
        _machines.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball", "GXYZ-1", "Godzilla", 2021, null)));
        _stats.GetByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns<Task<ManufacturerCatalogStats?>>(_ => throw new InvalidOperationException("stats down"));

        var cut = RenderPage("stern");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturer-games-table']");
        Assert.Contains("Godzilla", table.TextContent, StringComparison.Ordinal);
        Assert.Contains("—", table.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-browse-docs']"));
    }
}
