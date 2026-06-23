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
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminSourceDetail.razor (/admin/sources/{id}).
//
// Interactive (@rendermode InteractiveServer, ADR-0034 amendment): the load runs in
// OnAfterRenderAsync (bUnit invokes it on render); the two single-partition point-reads
// (ADR-0036) and their per-section failure isolation are unchanged from #2. These tests
// cover the read paths (all three sections, politeness defaults, n/a, not-found,
// load-failure, catalog isolation). The admin-gated enable/disable toggle is covered by
// the AdminSourceDetailToggle*/Anonymous contexts below.
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

        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        scrapeRuns.StreamBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyRuns());
        Services.AddScoped<AdminActionGuard>();
        Services.AddSingleton(sourceRepo);
        Services.AddSingleton(statsRepo);
        Services.AddSingleton(scrapeRuns);
        Services.AddSingleton<ILogger<AdminSourceDetail>>(NullLogger<AdminSourceDetail>.Instance);
    }

    private static async IAsyncEnumerable<ScrapeRunRecord> EmptyRuns()
    {
        await Task.CompletedTask;
        yield break;
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

// Admin-gated toggle — authorized (AdminOnly policy). MudSwitch renders for admins;
// triggering its change drives the guarded SetEnabledAsync mutation. The switch's <input>
// is the only input on the page, so cut.Find("input") targets it; the change is dispatched
// inside InvokeAsync (the dispatcher-click rule — finding the element outside the dispatcher
// risks a stale handler id under load).
public sealed class AdminSourceDetailToggleAuthorizedTests : AsyncBunitContext
{
    private const string SternId = "stern";
    private readonly IIngestionSourceRepository _sourceRepo = Substitute.For<IIngestionSourceRepository>();

    private static IngestionSource Source(bool enabled) => new()
    {
        Id = SternId, DisplayName = "Stern Pinball", ScraperImplKey = SternId,
        BaseUrl = "https://sternpinball.com", Enabled = enabled, Cadence = "weekly",
    };

    public AdminSourceDetailToggleAuthorizedTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com").SetPolicies("AdminOnly");
        Services.AddScoped<AdminActionGuard>();

        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo.GetByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ManufacturerCatalogStats?>(null));
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        scrapeRuns.StreamBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyRunsToggle());
        Services.AddSingleton(_sourceRepo);
        Services.AddSingleton(statsRepo);
        Services.AddSingleton(scrapeRuns);
        Services.AddSingleton<ILogger<AdminSourceDetail>>(NullLogger<AdminSourceDetail>.Instance);
    }

    private static async IAsyncEnumerable<ScrapeRunRecord> EmptyRunsToggle()
    {
        await Task.CompletedTask;
        yield break;
    }

    private IRenderedComponent<AdminSourceDetail> RenderDetail()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminSourceDetail>(1);
            builder.AddAttribute(2, nameof(AdminSourceDetail.Id), SternId);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminSourceDetail>();
    }

    [Fact]
    public async Task Authorized_RendersSwitch_NotChip()
    {
        _sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(Source(enabled: true)));
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotEmpty(cut.FindAll("[data-testid='source-enabled-switch']"));
        Assert.Empty(cut.FindAll("[data-testid='source-enabled-chip']"));
    }

    [Fact]
    public async Task ToggleOff_CallsSetEnabledFalse()
    {
        _sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(Source(enabled: true)));
        _sourceRepo.SetEnabledAsync(SternId, false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await cut.InvokeAsync(() => cut.Find("input").Change(false));

        cut.WaitForAssertion(() =>
            _sourceRepo.Received(1).SetEnabledAsync(SternId, false, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task ToggleOn_FromDisabled_CallsSetEnabledTrue()
    {
        _sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(Source(enabled: false)));
        _sourceRepo.SetEnabledAsync(SternId, true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await cut.InvokeAsync(() => cut.Find("input").Change(true));

        cut.WaitForAssertion(() =>
            _sourceRepo.Received(1).SetEnabledAsync(SternId, true, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task SetEnabledReturnsFalse_DoesNotChangeEnabledState()
    {
        // Source vanished between load and toggle → honest failure, no state lie.
        _sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(Source(enabled: true)));
        _sourceRepo.SetEnabledAsync(SternId, false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await cut.InvokeAsync(() => cut.Find("input").Change(false));

        // The switch input stays checked (Enabled unchanged) — no fabricated success.
        // HasAttribute("checked") reads the Blazor-rendered attribute (established pattern
        // in SettingsTests.cs:82); IsChecked tracks user-interaction state in AngleSharp
        // and stays false after .Change() even when the component reverts.
        cut.WaitForAssertion(() =>
        {
            _sourceRepo.Received(1).SetEnabledAsync(SternId, false, Arg.Any<CancellationToken>());
            Assert.True(cut.Find("input").HasAttribute("checked"));
        });
    }

    [Fact]
    public async Task SetEnabledThrows_DoesNotChangeEnabledState_NoUnhandledException()
    {
        _sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(Source(enabled: true)));
        _sourceRepo.SetEnabledAsync(SternId, false, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("simulated Cosmos failure"));
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await cut.InvokeAsync(() => cut.Find("input").Change(false));

        cut.WaitForAssertion(() =>
        {
            _sourceRepo.Received(1).SetEnabledAsync(SternId, false, Arg.Any<CancellationToken>());
            Assert.True(cut.Find("input").HasAttribute("checked"));
        });
    }
}

// Admin-gated toggle — anonymous. The switch is the UI boundary: a non-admin sees the
// read-only chip and no switch, so the mutation is unreachable from the UI. (The server
// boundary — Guard.IsAdminAsync at the top of the handler — is covered by AdminActionGuardTests.)
public sealed class AdminSourceDetailAnonymousToggleTests : AsyncBunitContext
{
    private const string SternId = "stern";

    public AdminSourceDetailAnonymousToggleTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization(); // NOT authorized → _isAdmin false
        Services.AddScoped<AdminActionGuard>();

        var sourceRepo = Substitute.For<IIngestionSourceRepository>();
        sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(new IngestionSource
            {
                Id = SternId, DisplayName = "Stern Pinball", ScraperImplKey = SternId,
                BaseUrl = "https://sternpinball.com", Enabled = true, Cadence = "weekly",
            }));
        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo.GetByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ManufacturerCatalogStats?>(null));
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        scrapeRuns.StreamBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyRunsAnon());
        Services.AddSingleton(sourceRepo);
        Services.AddSingleton(statsRepo);
        Services.AddSingleton(scrapeRuns);
        Services.AddSingleton<ILogger<AdminSourceDetail>>(NullLogger<AdminSourceDetail>.Instance);
    }

    private static async IAsyncEnumerable<ScrapeRunRecord> EmptyRunsAnon()
    {
        await Task.CompletedTask;
        yield break;
    }

    private IRenderedComponent<AdminSourceDetail> RenderDetail()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminSourceDetail>(1);
            builder.AddAttribute(2, nameof(AdminSourceDetail.Id), SternId);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminSourceDetail>();
    }

    [Fact]
    public async Task Anonymous_RendersChip_NotSwitch()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotEmpty(cut.FindAll("[data-testid='source-enabled-chip']"));
        Assert.Empty(cut.FindAll("[data-testid='source-enabled-switch']"));
    }
}

public sealed class AdminSourceDetailRunHistoryTests : AsyncBunitContext
{
    private const string SternId = "stern";
    private readonly IScrapeRunRepository _scrapeRuns = Substitute.For<IScrapeRunRepository>();

    private static IngestionSource Source() => new()
    {
        Id = SternId, DisplayName = "Stern Pinball", ScraperImplKey = SternId,
        BaseUrl = "https://sternpinball.com", Enabled = true, Cadence = "weekly",
    };

    private static async IAsyncEnumerable<ScrapeRunRecord> Runs(params ScrapeRunRecord[] runs)
    {
        await Task.CompletedTask;
        foreach (var r in runs) yield return r;
    }

    private static async IAsyncEnumerable<ScrapeRunRecord> Throwing()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public AdminSourceDetailRunHistoryTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddScoped<AdminActionGuard>();

        var sourceRepo = Substitute.For<IIngestionSourceRepository>();
        sourceRepo.GetByIdAsync(SternId, "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(Source()));
        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo.GetByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ManufacturerCatalogStats?>(null));
        Services.AddSingleton(sourceRepo);
        Services.AddSingleton(statsRepo);
        Services.AddSingleton(_scrapeRuns);
        Services.AddSingleton<ILogger<AdminSourceDetail>>(NullLogger<AdminSourceDetail>.Instance);
    }

    private IRenderedComponent<AdminSourceDetail> RenderDetail()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminSourceDetail>(1);
            builder.AddAttribute(2, nameof(AdminSourceDetail.Id), SternId);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminSourceDetail>();
    }

    [Fact]
    public async Task WithRuns_RendersTable()
    {
        _scrapeRuns.StreamBySourceAsync(SternId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Runs(
                new ScrapeRunRecord { SourceId = SternId, RunAt = new DateTimeOffset(2026, 6, 23, 8, 0, 0, TimeSpan.Zero), DurationSeconds = 12.4, Succeeded = true, DocumentsDiscovered = 7 },
                new ScrapeRunRecord { SourceId = SternId, RunAt = new DateTimeOffset(2026, 6, 22, 8, 0, 0, TimeSpan.Zero), DurationSeconds = 3.1, Succeeded = false, DocumentsDiscovered = 0, ErrorMessage = "timeout" }));
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var section = cut.Find("[data-testid='source-run-history']");
        Assert.Contains("7", section.TextContent, StringComparison.Ordinal);       // doc count
        Assert.Contains("timeout", section.TextContent, StringComparison.Ordinal); // error on failed row
    }

    [Fact]
    public async Task NoRuns_RendersEmptyState()
    {
        _scrapeRuns.StreamBySourceAsync(SternId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Runs());
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='run-history-empty']");
    }

    [Fact]
    public async Task ReadFailure_IsSectionIsolated()
    {
        _scrapeRuns.StreamBySourceAsync(SternId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Throwing());
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='run-history-failed']");
        // Section isolation (Invariant #17): config + politeness still render.
        cut.Find("[data-testid='source-config']");
        cut.Find("[data-testid='source-politeness']");
    }
}
