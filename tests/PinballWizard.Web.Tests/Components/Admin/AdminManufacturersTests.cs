using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminManufacturers.razor (/admin/manufacturers). Static SSR +
// [StreamRendering]: OnInitializedAsync reads the per-manufacturer rollups
// (StreamAllManufacturersAsync — point-reads) and enriches with ingestion-source
// name/status (StreamAllAsync — single 'config' partition), joined by key, sorted
// alphabetically. Honest states: rollup-fail alert, distinct empty, and best-effort
// enrichment that degrades a row to the raw key (Invariant #17).
public sealed class AdminManufacturersTests : AsyncBunitContext
{
    private readonly ICatalogStatsReadRepository _stats = Substitute.For<ICatalogStatsReadRepository>();
    private readonly IIngestionSourceRepository _sources = Substitute.For<IIngestionSourceRepository>();

    public AdminManufacturersTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddSingleton(_stats);
        Services.AddSingleton(_sources);
        Services.AddSingleton<ILogger<AdminManufacturers>>(NullLogger<AdminManufacturers>.Instance);
    }

    private static readonly DateTimeOffset AsOf = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static ManufacturerCatalogStats Rollup(string key, int machineCount, int docsEach = 3) => new(
        Manufacturer: key,
        AsOfUtc: AsOf,
        Machines: Enumerable.Range(0, machineCount).Select(i => new MachineDocStats(
            MachineId: $"{key}_m{i}", Title: "T", EditionLabel: null, GroupId: null, Year: 2021,
            IsOpdbOnly: false, DocCount: docsEach,
            DocTypeCounts: new Dictionary<string, int>(), HasManual: true)).ToList());

    private static IngestionSource Source(string id, string displayName, bool enabled) => new()
    {
        Id = id, DisplayName = displayName, ScraperImplKey = id,
        BaseUrl = $"https://{id}.example.com", Enabled = enabled, Cadence = "weekly",
    };

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

    private IRenderedComponent<AdminManufacturers> RenderPage()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminManufacturers>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminManufacturers>();
    }

    [Fact]
    public async Task Populated_RendersRowWithNameStatusCountsAndLink()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(Rollup("stern", 2, docsEach: 3)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(Source("stern", "Stern Pinball", enabled: true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Cell-specific (not a whole-table substring — "2"/"6" would otherwise collide with the date).
        var cells = cut.Find("[data-testid='manufacturers-table'] tbody tr").QuerySelectorAll("td");
        Assert.Contains("Stern Pinball", cells[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Enabled", cells[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("2", cells[2].TextContent.Trim());   // machines
        Assert.Equal("6", cells[3].TextContent.Trim());   // catalog documents = 2 * 3
        cut.Find("a[href='/admin/sources/stern']");
    }

    [Fact]
    public async Task Sorted_AlphabeticallyByDisplayName()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Rollup("stern", 1), Rollup("jjp", 1)));  // emit Stern first
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Source("stern", "Stern Pinball", true), Source("jjp", "Jersey Jack Pinball", true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var text = cut.Find("[data-testid='manufacturers-table'] tbody").TextContent;
        Assert.True(
            text.IndexOf("Jersey Jack", StringComparison.Ordinal) < text.IndexOf("Stern Pinball", StringComparison.Ordinal),
            "Rows must be sorted alphabetically by display name (no ranking).");
    }

    [Fact]
    public async Task Empty_RendersDistinctEmptyState()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<ManufacturerCatalogStats>());
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturers-empty']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-load-failed']"));
    }

    [Fact]
    public async Task RollupLoadFailure_RendersVisibleAlertNoTable()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>()).Returns(_ => Throwing<ManufacturerCatalogStats>());
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturers-load-failed']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-table']"));
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-empty']"));
    }

    [Fact]
    public async Task EnrichmentFailure_DegradesRowToKey_CountsStillRender()
    {
        // Sources read throws → best-effort: the row still renders with the raw key + neutral status,
        // and the real machine/doc counts survive (Invariant #17 — core data not blanked).
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(Rollup("stern", 2, docsEach: 3)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Throwing<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturers-table']");
        Assert.Contains("stern", table.TextContent, StringComparison.Ordinal);   // raw key, not "Stern Pinball"
        Assert.DoesNotContain("Stern Pinball", table.TextContent, StringComparison.Ordinal);
        Assert.Contains("6", table.TextContent, StringComparison.Ordinal);        // counts survive
    }

    [Fact]
    public async Task MissingSourceForKey_DegradesThatRowToKey()
    {
        // Rollup exists but no matching ingestion source → that row degrades to the key.
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(Rollup("ghostmfr", 1)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(Source("stern", "Stern Pinball", true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("ghostmfr", cut.Find("[data-testid='manufacturers-table']").TextContent, StringComparison.Ordinal);
    }
}
