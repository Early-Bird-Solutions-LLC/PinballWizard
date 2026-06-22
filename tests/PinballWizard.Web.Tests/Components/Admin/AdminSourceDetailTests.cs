using Bunit;
using Microsoft.AspNetCore.Components;
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

// bUnit tests for AdminSourceDetail.razor (/admin/sources/{id}).
//
// Static SSR + [StreamRendering] (ADR-0034): OnInitializedAsync runs two
// single-partition point-reads (ADR-0036) — the source by id, then the
// per-manufacturer catalog rollup. bUnit runs that synchronously. The tests
// assert the real load paths: all three sections render, politeness falls back
// to "using global default", a non-manufacturer source shows "n/a", an unknown
// id shows the distinct not-found state, a source-read failure shows the visible
// load-failed alert (Invariant #17), and a catalog-read failure is isolated to
// the contribution card while config/politeness still render.
public sealed class AdminSourceDetailTests : AsyncBunitContext
{
    private const string SternId = "stern";

    private static IngestionSource Source(
        string id = SternId,
        bool enabled = true,
        PolitenessOverrides? overrides = null) => new()
    {
        Id = id,
        DisplayName = "Stern Pinball",
        ScraperImplKey = id,
        BaseUrl = "https://sternpinball.com",
        Enabled = enabled,
        Cadence = "weekly",
        LastRunAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        LastSuccessAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        TotalDocumentsDiscovered = 42,
        TotalRunFailures = 1,
        PolitenessOverrides = overrides,
    };

    private static ManufacturerCatalogStats Stats(string manufacturer = SternId) => new(
        Manufacturer: manufacturer,
        AsOfUtc: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        Machines:
        [
            new MachineDocStats("mch_a", "Godzilla", "Pro", "godzilla", 2021,
                IsOpdbOnly: false, DocCount: 3,
                DocTypeCounts: new Dictionary<string, int> { ["Manual"] = 1 }, HasManual: true),
            new MachineDocStats("mch_b", "Godzilla", "LE", "godzilla", 2021,
                IsOpdbOnly: false, DocCount: 2,
                DocTypeCounts: new Dictionary<string, int>(), HasManual: false),
        ]);

    public AdminSourceDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
    }

    private void Setup(
        IngestionSource? source,
        ManufacturerCatalogStats? stats = null,
        bool sourceThrows = false,
        bool statsThrows = false)
    {
        var sourceRepo = Substitute.For<IIngestionSourceRepository>();
        if (sourceThrows)
        {
            sourceRepo.GetByIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task<IngestionSource?>>(_ => throw new InvalidOperationException("simulated Cosmos failure"));
        }
        else
        {
            sourceRepo.GetByIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(source));
        }

        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        if (statsThrows)
        {
            statsRepo.GetByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task<ManufacturerCatalogStats?>>(_ => throw new InvalidOperationException("simulated Cosmos failure"));
        }
        else
        {
            statsRepo.GetByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(stats));
        }

        Services.AddSingleton(sourceRepo);
        Services.AddSingleton(statsRepo);
        Services.AddSingleton<ILogger<AdminSourceDetail>>(NullLogger<AdminSourceDetail>.Instance);
    }

    // MudBlazor 9 requires a MudPopoverProvider sibling for popover-capable
    // components (MudBreadcrumbs/MudChip). Pass the route param Id via attribute
    // (bUnit doesn't parse @page templates).
    private IRenderedComponent<AdminSourceDetail> RenderDetail(string id)
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminSourceDetail>(1);
            builder.AddAttribute(2, nameof(AdminSourceDetail.Id), id);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminSourceDetail>();
    }

    [Fact]
    public async Task ManufacturerSource_RendersAllThreeSections()
    {
        Setup(Source(overrides: new PolitenessOverrides { RequestDelayMs = 1500 }), Stats());

        var cut = RenderDetail(SternId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='source-config']");
        cut.Find("[data-testid='source-politeness']");
        var catalog = cut.Find("[data-testid='source-catalog']");
        Assert.Contains("Stern Pinball", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("1500", cut.Markup, StringComparison.Ordinal);   // override value shown
        Assert.Contains("2", catalog.TextContent, StringComparison.Ordinal); // machine count = 2
        Assert.Contains("5", catalog.TextContent, StringComparison.Ordinal); // total docs = 3 + 2
    }

    [Fact]
    public async Task NullPoliteness_ShowsGlobalDefaultForEachField()
    {
        Setup(Source(overrides: null), Stats());

        var cut = RenderDetail(SternId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        var panel = cut.Find("[data-testid='source-politeness']");
        // Four overridable fields all fall back to the same sentinel phrase.
        var count = panel.TextContent.Split("using global default").Length - 1;
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task NonManufacturerSource_ShowsCatalogNotApplicable()
    {
        // stats null = GetByManufacturerAsync returned null (e.g. OPDB).
        Setup(Source(id: "opdb"), stats: null);

        var cut = RenderDetail("opdb");
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='catalog-na']");
        // Config + politeness still render.
        cut.Find("[data-testid='source-config']");
        cut.Find("[data-testid='source-politeness']");
    }

    [Fact]
    public async Task UnknownId_RendersNotFoundState()
    {
        Setup(source: null);

        var cut = RenderDetail("does-not-exist");
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='source-not-found']");
        // Not-found must NOT masquerade as a load failure.
        Assert.Empty(cut.FindAll("[data-testid='source-detail-load-failed']"));
    }

    [Fact]
    public async Task SourceLoadFailure_RendersVisibleErrorAndNoSections()
    {
        Setup(source: null, sourceThrows: true);

        var cut = RenderDetail(SternId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='source-detail-load-failed']");
        // A failure is distinct from not-found and from the rendered sections.
        Assert.Empty(cut.FindAll("[data-testid='source-not-found']"));
        Assert.Empty(cut.FindAll("[data-testid='source-config']"));
    }

    [Fact]
    public async Task CatalogLoadFailure_IsolatedToContributionCard()
    {
        Setup(Source(), stats: null, statsThrows: true);

        var cut = RenderDetail(SternId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='catalog-load-failed']");
        // Section isolation (Invariant #17): config + politeness still render.
        cut.Find("[data-testid='source-config']");
        cut.Find("[data-testid='source-politeness']");
    }
}
