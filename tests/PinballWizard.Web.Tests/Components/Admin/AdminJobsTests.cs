using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Jobs;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminJobs.razor (/admin/jobs).
//
// Tests assert the three honest states of the page:
//   1. Populated — ARM returns a job list → table renders with job names + statuses
//   2. ARM error (ArmJobAdminException) — renders the visible error alert, NOT the table
//   3. Run-now dialog — confirm button calls StartJobAsync once (dispatcher-click pattern)
//   4. Service unavailable (IJobAdminService not registered) — info alert shown
//
// The page uses nullable injection (@inject IJobAdminService? JobService) so the
// test can register or withhold the service to exercise each path.
public sealed class AdminJobsTests : AsyncBunitContext
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<JobStatus> SeedJobs =
    [
        new JobStatus(
            JobName: "pinwiz-job-linker-buutj",
            DisplayName: "Linker",
            CronExpression: "0 2 * * *",
            TriggerType: "Schedule",
            LatestExecutionStatus: "Succeeded",
            LatestExecutionStartTime: AsOf),
        new JobStatus(
            JobName: "pinwiz-job-opdb-buutj",
            DisplayName: "Opdb",
            CronExpression: "0 3 * * 0",
            TriggerType: "Schedule",
            LatestExecutionStatus: "Failed",
            LatestExecutionStartTime: AsOf.AddDays(-7)),
    ];

    public AdminJobsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddSingleton<ILogger<AdminJobs>>(NullLogger<AdminJobs>.Instance);
    }

    // Render the page with a controllable IJobAdminService (or none).
    // Returns the AdminJobs component directly. The MudPopoverProvider sibling is
    // rendered to satisfy MudBlazor's popover requirements (MudSnackbarProvider, etc.).
    private IRenderedComponent<AdminJobs> RenderPage(IJobAdminService? svc = null)
    {
        if (svc is not null) Services.AddSingleton(svc);
        // When svc is null, IJobAdminService is not registered. bUnit resolves
        // nullable @inject as null if the service is absent from the DI container.

        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminJobs>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminJobs>();
    }

    // Helper: flush the async load that runs in OnAfterRenderAsync.
    private static async Task FlushAsync(IRenderedComponent<AdminJobs> cut)
        => await cut.InvokeAsync(() => Task.CompletedTask);

    // ── State 1: populated ───────────────────────────────────────────────────

    [Fact]
    public async Task Populated_RendersJobTable_WithJobNamesAndStatuses()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>(SeedJobs));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Table exists; no error / empty / unavailable states.
        cut.Find("[data-testid='jobs-table']");
        Assert.Empty(cut.FindAll("[data-testid='jobs-arm-error']"));
        Assert.Empty(cut.FindAll("[data-testid='jobs-empty']"));
        Assert.Empty(cut.FindAll("[data-testid='jobs-service-unavailable']"));

        // Both job names appear somewhere in the rendered output.
        Assert.Contains("Linker", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Opdb", cut.Markup, StringComparison.Ordinal);

        // Status chips are rendered.
        var statusChips = cut.FindAll("[data-testid='job-status']");
        Assert.Equal(2, statusChips.Count);
        Assert.Contains(statusChips, c => c.TextContent.Contains("Succeeded", StringComparison.Ordinal));
        Assert.Contains(statusChips, c => c.TextContent.Contains("Failed", StringComparison.Ordinal));
    }

    // ── State 2: ARM error ───────────────────────────────────────────────────

    [Fact]
    public async Task ArmError_RendersVisibleAlert_NotTable()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<JobStatus>>>(_ =>
                throw new ArmJobAdminException("Unauthorized — missing RBAC"));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // The ARM error alert must be visible — degrade visibly per Invariant #17.
        cut.Find("[data-testid='jobs-arm-error']");
        // The table must NOT render (no fake/placeholder data).
        Assert.Empty(cut.FindAll("[data-testid='jobs-table']"));
        Assert.Empty(cut.FindAll("[data-testid='jobs-service-unavailable']"));
    }

    // ── State 3: service not registered (local dev) ──────────────────────────
    // bUnit resolves @inject IJobAdminService? as null when no service is registered,
    // which triggers the _serviceUnavailable path. Verified: Blazor's DI resolves
    // nullable-annotated @inject as null when the service is absent.

    [Fact]
    public async Task ServiceNotRegistered_RendersInfoAlert()
    {
        // Do NOT register IJobAdminService — the page uses nullable injection.
        var cut = RenderPage(svc: null);
        await FlushAsync(cut);

        cut.Find("[data-testid='jobs-service-unavailable']");
        Assert.Empty(cut.FindAll("[data-testid='jobs-arm-error']"));
        Assert.Empty(cut.FindAll("[data-testid='jobs-table']"));
    }

    // ── State 4: empty job list ───────────────────────────────────────────────

    [Fact]
    public async Task EmptyJobList_RendersEmptyState()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>([]));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='jobs-empty']");
        Assert.Empty(cut.FindAll("[data-testid='jobs-table']"));
    }

    // ── State 5: run-now confirm dialog calls StartJobAsync once ─────────────

    [Fact]
    public async Task RunNow_ConfirmDialog_CallsStartJobOnce()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>(SeedJobs));
        svc.StartJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Click the first "Run now" button — must Find INSIDE InvokeAsync to
        // avoid a stale handler ID under load (UnknownEventHandlerIdException
        // flake class — see project_bunit_dispatcher_click_pattern memory).
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='run-now-button']").Click());

        // MudDialog @bind-Visible renders its content inline in the component DOM;
        // after clicking Run now the dialog is visible and its buttons are in cut.Markup.
        await cut.InvokeAsync(() => Task.CompletedTask); // flush state change

        var confirmBtn = cut.Find("[data-testid='confirm-run']");

        // Click Confirm — again inside the dispatcher.
        await cut.InvokeAsync(() => confirmBtn.Click());

        // StartJobAsync called exactly once for the first job.
        await svc.Received(1).StartJobAsync(
            SeedJobs[0].JobName, Arg.Any<CancellationToken>());
    }

    // ── State 6: Cancel in dialog does NOT call StartJobAsync ────────────────

    [Fact]
    public async Task RunNow_CancelDialog_DoesNotCallStart()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>(SeedJobs));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='run-now-button']").Click());

        await cut.InvokeAsync(() => Task.CompletedTask); // flush state change

        var cancelBtn = cut.Find("[data-testid='confirm-cancel']");
        await cut.InvokeAsync(() => cancelBtn.Click());

        await svc.DidNotReceive().StartJobAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Row click navigates to detail page ───────────────────────────────────

    [Fact]
    public async Task RowClick_NavigatesToJobDetailPage()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>(SeedJobs));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Click the first body row (skip the header row at index 0).
        var rows = cut.FindAll("tr");
        var firstDataRow = rows.Skip(1).First();
        await cut.InvokeAsync(() => firstDataRow.Click());

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        Assert.EndsWith($"/admin/jobs/{SeedJobs[0].JobName}", nav.Uri);
    }

    // ── Run Now button stopPropagation — click does NOT navigate ─────────────

    [Fact]
    public async Task RunNowButton_Click_DoesNotNavigate()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>(SeedJobs));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        var initialUri = nav.Uri;

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='run-now-button']").Click());

        // Navigation must NOT have occurred — we're still on the jobs list page.
        Assert.Equal(initialUri, nav.Uri);
    }

    // ── Cron column shows human-readable text, not raw expression ─────────────

    [Fact]
    public async Task CronColumn_ShowsHumanReadableExpression()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JobStatus>>(SeedJobs));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // "0 2 * * *" → "Daily at 2:00 AM UTC"
        Assert.Contains("Daily at 2:00 AM UTC", cut.Markup, StringComparison.Ordinal);
        // "0 3 * * 0" → "Sundays at 3:00 AM UTC"
        Assert.Contains("Sundays at 3:00 AM UTC", cut.Markup, StringComparison.Ordinal);
        // Raw expressions should NOT appear as standalone text
        Assert.DoesNotContain(">0 2 * * *<", cut.Markup, StringComparison.Ordinal);
    }
}
