using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Jobs;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class AdminJobDetailTests : AsyncBunitContext
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 26, 2, 0, 0, TimeSpan.Zero);

    private static JobDetail MakeDetail(bool hasMore = false, int executionCount = 2) =>
        new(
            JobName: "pinwiz-job-linker-buutj",
            DisplayName: "Linker",
            CronExpression: "0 2 * * *",
            TriggerType: "Schedule",
            LatestExecutionStatus: "Succeeded",
            ImageTag: "pinwizacrbuutj.azurecr.io/pinballwizard-cli:1.2.3",
            Executions: Enumerable.Range(0, executionCount)
                .Select(i => new JobExecution(
                    ExecutionName: $"pinwiz-job-linker-buutj--exec{i:D3}",
                    Status: i == 0 ? "Succeeded" : "Failed",
                    StartOn: AsOf.AddDays(-i),
                    EndOn: AsOf.AddDays(-i).AddMinutes(2)))
                .ToList(),
            HasMore: hasMore);

    public AdminJobDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com")
            .SetPolicies(PinballWizard.Web.Security.AuthorizationPolicies.AdminOnly);
        Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();
        Services.AddSingleton<ILogger<AdminJobDetail>>(NullLogger<AdminJobDetail>.Instance);
    }

    private IRenderedComponent<AdminJobDetail> RenderPage(
        IJobAdminService? svc = null, string jobName = "pinwiz-job-linker-buutj")
    {
        if (svc is not null) Services.AddSingleton(svc);

        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminJobDetail>(1);
            builder.AddAttribute(2, "JobName", jobName);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminJobDetail>();
    }

    private static async Task FlushAsync(IRenderedComponent<AdminJobDetail> cut)
        => await cut.InvokeAsync(() => Task.CompletedTask);

    // ── Populated state ───────────────────────────────────────────────────────

    [Fact]
    public async Task Populated_RendersGridSearchBox()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='grid-search-input']");
    }

    [Fact]
    public async Task Populated_RendersHeaderFields()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='job-detail-header']");
        // Human-readable cron
        Assert.Contains("Daily at 2:00 AM UTC", cut.Markup, StringComparison.Ordinal);
        // Image tag in header
        Assert.Contains("pinwizacrbuutj.azurecr.io/pinballwizard-cli:1.2.3", cut.Markup, StringComparison.Ordinal);
        // Latest status chip
        cut.Find("[data-testid='latest-status']");
    }

    [Fact]
    public async Task Populated_RendersExecutionHistoryTable()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='execution-table']");
        var statusChips = cut.FindAll("[data-testid='execution-status']");
        Assert.Equal(2, statusChips.Count);
        Assert.Contains(statusChips, c => c.TextContent.Contains("Succeeded", StringComparison.Ordinal));
        Assert.Contains(statusChips, c => c.TextContent.Contains("Failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Populated_DurationShownForCompletedExecutions()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Both executions have StartOn and EndOn set (2 minutes apart) → "2m 0s"
        Assert.Contains("2m 0s", cut.Markup, StringComparison.Ordinal);
    }

    // ── Load more ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadMoreButton_HiddenWhenHasMoreIsFalse()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail(hasMore: false)));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        Assert.Empty(cut.FindAll("[data-testid='load-more-button']"));
    }

    [Fact]
    public async Task LoadMoreButton_VisibleWhenHasMoreIsTrue()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail(hasMore: true)));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='load-more-button']");
    }

    [Fact]
    public async Task LoadMoreButton_Click_CallsGetJobDetailWithIncreasedCount()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail(hasMore: true)));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='load-more-button']").Click());
        await FlushAsync(cut);

        // First call: count=10, second call after Load more: count=20
        await svc.Received(1).GetJobDetailAsync(
            "pinwiz-job-linker-buutj", 10, Arg.Any<CancellationToken>());
        await svc.Received(1).GetJobDetailAsync(
            "pinwiz-job-linker-buutj", 20, Arg.Any<CancellationToken>());
    }

    // ── Error states ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ArmError_RendersErrorAlert()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<JobDetail>>(_ => throw new ArmJobAdminException("ARM failure"));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='job-detail-arm-error']");
        Assert.Empty(cut.FindAll("[data-testid='job-detail-header']"));
    }

    [Fact]
    public async Task NotFound_RendersNotFoundAlert()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<JobDetail>>(_ =>
                throw new ArmJobAdminException("Job 'x' was not found.", isNotFound: true));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='job-detail-not-found']");
        Assert.Empty(cut.FindAll("[data-testid='job-detail-arm-error']"));
        Assert.Empty(cut.FindAll("[data-testid='job-detail-header']"));
    }

    [Fact]
    public async Task ServiceNotRegistered_RendersUnavailableAlert()
    {
        var cut = RenderPage(svc: null);
        await FlushAsync(cut);

        cut.Find("[data-testid='job-detail-service-unavailable']");
        Assert.Empty(cut.FindAll("[data-testid='job-detail-header']"));
    }

    // ── Schedule edit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EditScheduleButton_AdminWithCronExpression_IsVisible()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Admin user + CronExpression present → edit button must render
        cut.Find("[data-testid='edit-schedule-button']");
    }

    [Fact]
    public async Task EditScheduleButton_CronExpressionNull_IsHidden()
    {
        var svc = Substitute.For<IJobAdminService>();
        var detailWithNoCron = new JobDetail(
            JobName: "pinwiz-job-linker-buutj",
            DisplayName: "Linker",
            CronExpression: null,
            TriggerType: "Manual",
            LatestExecutionStatus: "Succeeded",
            ImageTag: null,
            Executions: [],
            HasMore: false);
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(detailWithNoCron));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // No CronExpression → edit button must not render even for admin
        Assert.Empty(cut.FindAll("[data-testid='edit-schedule-button']"));
    }

    [Fact]
    public async Task EditScheduleButton_NonAdmin_IsHidden()
    {
        // Override the constructor's admin setup: render as an authenticated
        // user who does NOT satisfy the AdminOnly policy.
        this.AddAuthorization().SetNotAuthorized();

        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail())); // CronExpression is set

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Non-admin → edit button must be hidden even though CronExpression exists
        Assert.Empty(cut.FindAll("[data-testid='edit-schedule-button']"));
    }

    [Fact]
    public async Task EditButtonClick_OpensPanelPrefilledWithCurrentCron()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='edit-schedule-button']").Click());
        await FlushAsync(cut);

        // Panel must be present
        cut.Find("[data-testid='schedule-edit-panel']");

        // Input pre-filled with the current cron expression
        // MudBaseInput splats UserAttributes onto the <input> itself
        var cronInput = cut.Find("[data-testid='cron-input']");
        Assert.Equal("0 2 * * *", cronInput.GetAttribute("value"));

        // Preview shows the human-readable form
        // CronExpressionFormatter.Format("0 2 * * *") == "Daily at 2:00 AM UTC" (see CronExpressionFormatterTests)
        var preview = cut.Find("[data-testid='cron-preview']");
        Assert.Contains("Daily at 2:00 AM UTC", preview.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSchedule_ValidExpression_CallsUpdateScheduleAsyncAndClosesPanel()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));
        svc.UpdateScheduleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Open the panel
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='edit-schedule-button']").Click());
        await FlushAsync(cut);

        // Change to a different valid expression
        // The cron-input MudTextField has Immediate="true", which wires TextChanged
        // to oninput (not onchange) — use .Input(), not .Change().
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='cron-input']").Input("0 5 * * 0"));

        // Save
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='schedule-edit-save']").Click());
        await FlushAsync(cut);

        // Service must have been called with the trimmed new expression
        await svc.Received(1).UpdateScheduleAsync(
            "pinwiz-job-linker-buutj", "0 5 * * 0", Arg.Any<CancellationToken>());

        // Panel must be closed
        Assert.Empty(cut.FindAll("[data-testid='schedule-edit-panel']"));
    }

    [Fact]
    public async Task SaveSchedule_InvalidExpression_ShowsInlineErrorAndDoesNotCallService()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Open the panel
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='edit-schedule-button']").Click());
        await FlushAsync(cut);

        // Enter an expression that CronExpressionValidator rejects (not 5 fields)
        // The cron-input MudTextField has Immediate="true", which wires TextChanged
        // to oninput (not onchange) — use .Input(), not .Change().
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='cron-input']").Input("not a cron"));

        // Click Save
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='schedule-edit-save']").Click());
        await FlushAsync(cut);

        // Inline error must appear in the rendered markup
        Assert.Contains("exactly 5 fields", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // UpdateScheduleAsync must never have been called
        await svc.DidNotReceive().UpdateScheduleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveSchedule_ArmFailure_ShowsErrorAndKeepsPanelOpen()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));
        // Mirror the throw pattern from ArmError_RendersErrorAlert
        svc.UpdateScheduleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ArmJobAdminException("ARM failure"));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Open the panel and save immediately (expression is pre-filled and valid)
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='edit-schedule-button']").Click());
        await FlushAsync(cut);

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='schedule-edit-save']").Click());
        await FlushAsync(cut);

        // UpdateScheduleAsync was called exactly once
        await svc.Received(1).UpdateScheduleAsync(
            "pinwiz-job-linker-buutj", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Panel stays open on ARM failure (SaveScheduleAsync does not close it in the catch block)
        cut.Find("[data-testid='schedule-edit-panel']");
    }

    [Fact]
    public async Task CancelButton_Click_ClosesPanelWithoutCallingService()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        // Open the panel
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='edit-schedule-button']").Click());
        await FlushAsync(cut);

        // Cancel
        await cut.InvokeAsync(() =>
            cut.Find("[data-testid='schedule-edit-cancel']").Click());
        await FlushAsync(cut);

        // Panel must be closed
        Assert.Empty(cut.FindAll("[data-testid='schedule-edit-panel']"));

        // UpdateScheduleAsync must never have been called
        await svc.DidNotReceive().UpdateScheduleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Execution row linking ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecutionRow_LinksToExecutionDetail()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new JobDetail("pinwiz-job-linker-buutj", "Linker", "0 2 * * *", "Schedule",
                "Succeeded", "img:tag",
                [new JobExecution("pinwiz-job-linker-buutj-29715960", "Succeeded",
                    DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow)],
                HasMore: false));

        var cut = RenderPage(svc, "pinwiz-job-linker-buutj");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var link = cut.Find("[data-testid='execution-link']");
        Assert.Equal("/admin/jobs/pinwiz-job-linker-buutj/executions/pinwiz-job-linker-buutj-29715960",
            link.GetAttribute("href"));
    }
}
