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

// bUnit tests for AdminManufacturers.razor (/admin/manufacturers).
//
// The page switched from IMachineRepository.StreamAllAsync (cross-partition) to
// ICatalogStatsReadRepository.StreamAllManufacturersAsync (bounded ~8-9 point reads,
// ADR-0036 Tier-1) so the test setup uses that repository now.
//
// Verified behavioural invariants:
// - Manufacturer name cell links to /admin/machines?manufacturer={key} (admin drill-down)
// - Documents column links to /admin/documents?manufacturer={key}
// - Documents count = sum of MachineDocStats.DocCount across the manufacturer's machines
// - Alphabetical ordering (no ranking — favouritism guardrail)
// - Enabled-status enrichment degrades visibly on source-read failure (Invariant #17)
// - Machine-load failure shows alert; empty set shows empty state
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

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Creates a ManufacturerCatalogStats with N machines whose DocCount values are
    // taken from the supplied array. Pass no doc counts for a zero-machine rollup.
    private static ManufacturerCatalogStats MfrStats(
        string key, string displayName, params int[] docCounts)
    {
        var machines = docCounts
            .Select((dc, i) => new MachineDocStats(
                MachineId:     $"mch_{key}_{i}",
                Title:         $"{displayName} Machine {i}",
                EditionLabel:  null,
                GroupId:       null,
                Year:          2024,
                IsOpdbOnly:    false,
                DocCount:      dc,
                DocTypeCounts: dc > 0
                    ? new Dictionary<string, int> { ["Manual"] = dc }
                    : new Dictionary<string, int>(),
                HasManual:     dc > 0))
            .ToList();

        return new ManufacturerCatalogStats(
            Manufacturer: key,
            AsOfUtc: DateTimeOffset.UtcNow,
            Machines: machines)
        { ManufacturerDisplayName = displayName };
    }

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

    // ── Core rendering ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Populated_RendersRowWithNameStatusCountAndAdminLink()
    {
        // Two stern machines: docCounts 1 + 1 → Machines=2, Documents=2
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(MfrStats("stern", "Stern Pinball", 1, 1)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Source("stern", "Stern Pinball", enabled: true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var cells = cut.Find("[data-testid='manufacturers-table'] tbody tr").QuerySelectorAll("td");
        Assert.Contains("Stern Pinball", cells[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Enabled",       cells[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("2",                cells[2].TextContent.Trim());   // Machines
        Assert.Equal("2",                cells[3].TextContent.Trim());   // Documents (sum)

        // Manufacturer name links to admin drill-down, not the public manufacturer page
        cut.Find("a[href='/admin/machines?manufacturer=stern']");
    }

    [Fact]
    public async Task Populated_DocumentsCell_LinksToAdminDocumentsFilter()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(MfrStats("stern", "Stern Pinball", 1, 1)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Documents column renders a link to /admin/documents?manufacturer={key}
        cut.Find("a[href='/admin/documents?manufacturer=stern']");
    }

    [Fact]
    public async Task Populated_DocumentsCount_IsSumOfMachineDocCounts()
    {
        // Three machines with doc counts 3, 5, 2 → Documents total = 10
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(MfrStats("stern", "Stern Pinball", 3, 5, 2)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var cells = cut.Find("[data-testid='manufacturers-table'] tbody tr").QuerySelectorAll("td");
        Assert.Equal("3", cells[2].TextContent.Trim());   // 3 machines
        Assert.Equal("10", cells[3].TextContent.Trim());  // 3+5+2 = 10 docs
    }

    [Fact]
    public async Task Sorted_AlphabeticallyByDisplayName()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                MfrStats("stern", "Stern Pinball", 1),
                MfrStats("jjp",   "Jersey Jack Pinball", 1)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var text = cut.Find("[data-testid='manufacturers-table'] tbody").TextContent;
        Assert.True(
            text.IndexOf("Jersey Jack", StringComparison.Ordinal) < text.IndexOf("Stern Pinball", StringComparison.Ordinal),
            "Rows must be sorted alphabetically by display name (no ranking).");
    }

    // ── Degraded / error states ────────────────────────────────────────────────

    [Fact]
    public async Task Empty_RendersDistinctEmptyState()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<ManufacturerCatalogStats>());
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturers-empty']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-load-failed']"));
    }

    [Fact]
    public async Task StatsLoadFailure_RendersVisibleAlertNoTable()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Throwing<ManufacturerCatalogStats>());
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturers-load-failed']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-table']"));
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-empty']"));
    }

    [Fact]
    public async Task SourceEnrichmentFailure_DisplayNameFromRollup_EnabledShowsDash()
    {
        // Source read throws → display name still comes from the rollup doc (no
        // raw-key degradation), Enabled shows "—", machine and doc counts survive.
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(MfrStats("stern", "Stern Pinball", 1, 1)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Throwing<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturers-table']");
        Assert.Contains("Stern Pinball", table.TextContent, StringComparison.Ordinal);
        var cells = table.QuerySelectorAll("tbody tr td");
        Assert.Equal("2", cells[2].TextContent.Trim());   // Machines survives
        Assert.DoesNotContain("Enabled", cells[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSourceForKey_StillLinksToAdminMachinesFilter()
    {
        // Machine exists but no matching ingestion source → display name from rollup,
        // status "—", but the admin machines filter link still renders.
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(MfrStats("williams", "Williams", 0)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Source("stern", "Stern Pinball", true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturers-table']");
        Assert.Contains("Williams", table.TextContent, StringComparison.Ordinal);
        cut.Find("a[href='/admin/machines?manufacturer=williams']");
    }

    [Fact]
    public async Task PagingAt10_RendersOnlyFirstPageWhenMoreThan10Manufacturers()
    {
        // 11 distinct manufacturers — page 1 shows 10 rows (default PageSize), pager renders.
        var stats = Enumerable.Range(1, 11)
            .Select(i => MfrStats($"mfr{i:D2}", $"Manufacturer {i:D2}", 1))
            .ToArray();
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(stats));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var rows = cut.FindAll("[data-testid='manufacturers-table'] tbody tr");
        Assert.Equal(10, rows.Count);
        cut.Find(".mud-table-pagination");
    }

    [Fact]
    public async Task MultipleManufacturers_RendersCorrectCounts()
    {
        // Stern has 3 machines (docs 1,1,1=3), JJP has 1 machine (docs 2).
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                MfrStats("stern", "Stern Pinball", 1, 1, 1),
                MfrStats("jjp",   "Jersey Jack Pinball", 2)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var rows = cut.FindAll("[data-testid='manufacturers-table'] tbody tr");
        Assert.Equal(2, rows.Count);

        // Jersey Jack sorts before Stern alphabetically
        var jjpCells  = rows.First(r => r.TextContent.Contains("Jersey Jack", StringComparison.Ordinal))
                            .QuerySelectorAll("td");
        var sternCells = rows.First(r => r.TextContent.Contains("Stern Pinball", StringComparison.Ordinal))
                             .QuerySelectorAll("td");

        Assert.Equal("1", jjpCells[2].TextContent.Trim());    // JJP: 1 machine
        Assert.Equal("2", jjpCells[3].TextContent.Trim());    // JJP: 2 docs
        Assert.Equal("3", sternCells[2].TextContent.Trim());  // Stern: 3 machines
        Assert.Equal("3", sternCells[3].TextContent.Trim());  // Stern: 1+1+1 = 3 docs
    }

    [Fact]
    public async Task Populated_GridSearchBox_UsesAdminManufacturersContext()
    {
        _stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(MfrStats("stern", "Stern Pinball", 1)));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var gridSearch = cut.FindComponent<PinballWizard.Web.Components.Shared.GridSearch>();
        Assert.Equal("admin-manufacturers", gridSearch.Instance.Context);
    }
}
