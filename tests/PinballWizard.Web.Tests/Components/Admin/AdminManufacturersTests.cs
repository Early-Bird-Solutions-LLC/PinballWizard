using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminManufacturers.razor (/admin/manufacturers). Interactive
// (rendermode InteractiveServer): OnInitializedAsync streams all machines from
// IMachineRepository (cross-partition, ADR-0036 allow-listed) and groups by
// manufacturer partition key, then enriches rows with Enabled status from
// IIngestionSourceRepository (best-effort, single 'config' partition).
// ManufacturerDisplayName comes from the machine records so rows never degrade
// to the raw key. Honest states: machine-load-fail alert, distinct empty, and
// source-enrichment failure that leaves Enabled = "—" while counts + display
// names survive (Invariant #17).
public sealed class AdminManufacturersTests : AsyncBunitContext
{
    private readonly IMachineRepository _machines = Substitute.For<IMachineRepository>();
    private readonly IIngestionSourceRepository _sources = Substitute.For<IIngestionSourceRepository>();

    public AdminManufacturersTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddSingleton(_machines);
        Services.AddSingleton(_sources);
        Services.AddSingleton<ILogger<AdminManufacturers>>(NullLogger<AdminManufacturers>.Instance);
    }

    private static Machine M(string key, string displayName) => new()
    {
        Id                      = $"{key}-opdb-id",
        PartitionKey            = key,
        ManufacturerDisplayName = displayName,
        Title                   = "Test Machine",
        FirstSeenAt             = DateTimeOffset.MinValue,
        LastSeenAt              = DateTimeOffset.MinValue,
    };

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
    public async Task Populated_RendersRowWithNameStatusCountAndSourceLink()
    {
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball"), M("stern", "Stern Pinball")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Source("stern", "Stern Pinball", enabled: true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var cells = cut.Find("[data-testid='manufacturers-table'] tbody tr").QuerySelectorAll("td");
        Assert.Contains("Stern Pinball", cells[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Enabled", cells[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("2", cells[2].TextContent.Trim());
        cut.Find("a[href='/admin/sources/stern']");
    }

    [Fact]
    public async Task Sorted_AlphabeticallyByDisplayName()
    {
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball"), M("jjp", "Jersey Jack Pinball")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream<IngestionSource>());

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
        _machines.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<Machine>());
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturers-empty']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-load-failed']"));
    }

    [Fact]
    public async Task MachineLoadFailure_RendersVisibleAlertNoTable()
    {
        _machines.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Throwing<Machine>());
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturers-load-failed']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-table']"));
        Assert.Empty(cut.FindAll("[data-testid='manufacturers-empty']"));
    }

    [Fact]
    public async Task SourceEnrichmentFailure_DisplayNameFromMachine_EnabledShowsDash()
    {
        // Source read throws → display name still comes from machine record (no raw-key
        // degradation), Enabled shows "—", machine count survives (Invariant #17).
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball"), M("stern", "Stern Pinball")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Throwing<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturers-table']");
        Assert.Contains("Stern Pinball", table.TextContent, StringComparison.Ordinal);
        var cells = table.QuerySelectorAll("tbody tr td");
        Assert.Equal("2", cells[2].TextContent.Trim());
        Assert.DoesNotContain("Enabled", cells[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSourceForKey_PlainTextDisplayName_NoSourceLink()
    {
        // Machine exists but no matching ingestion source → display name from machine,
        // status "—", no source link (OPDB-only manufacturer).
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("williams", "Williams")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Source("stern", "Stern Pinball", true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturers-table']");
        Assert.Contains("Williams", table.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("a[href='/admin/sources/williams']"));
    }

    [Fact]
    public async Task PagingAt25_RendersOnlyFirstPageWhenMoreThan25Manufacturers()
    {
        // 26 distinct manufacturers — page 1 should show exactly 25 rows; pager footer must render.
        var machines = Enumerable.Range(1, 26)
            .Select(i => M($"mfr{i:D2}", $"Manufacturer {i:D2}"))
            .ToArray();
        _machines.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(machines));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var rows = cut.FindAll("[data-testid='manufacturers-table'] tbody tr");
        Assert.Equal(25, rows.Count);
        cut.Find(".mud-table-pagination");
    }

    [Fact]
    public async Task MultipleManufacturers_GroupsMachinesCorrectly()
    {
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                M("stern", "Stern Pinball"), M("stern", "Stern Pinball"), M("stern", "Stern Pinball"),
                M("jjp", "Jersey Jack Pinball")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var rows = cut.FindAll("[data-testid='manufacturers-table'] tbody tr");
        Assert.Equal(2, rows.Count);

        var sternCells = rows.First(r => r.TextContent.Contains("Stern Pinball", StringComparison.Ordinal))
            .QuerySelectorAll("td");
        Assert.Equal("3", sternCells[2].TextContent.Trim());

        var jjpCells = rows.First(r => r.TextContent.Contains("Jersey Jack", StringComparison.Ordinal))
            .QuerySelectorAll("td");
        Assert.Equal("1", jjpCells[2].TextContent.Trim());
    }
}
