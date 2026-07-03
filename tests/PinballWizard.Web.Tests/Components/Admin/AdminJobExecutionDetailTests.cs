using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Jobs;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class AdminJobExecutionDetailTests : AsyncBunitContext
{
    private const string Job = "pinwiz-job-linker-buutj";
    private const string Exec = "pinwiz-job-linker-buutj-29715960";

    private readonly IJobAdminService _svc = Substitute.For<IJobAdminService>();
    private readonly IJobLogReader _logs = Substitute.For<IJobLogReader>();

    public AdminJobExecutionDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();
        Services.AddSingleton(_svc);
        Services.AddSingleton(_logs);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AdminJobExecutionDetail>>(
            NullLogger<AdminJobExecutionDetail>.Instance);
        _svc.GetExecutionAsync(Job, Exec, Arg.Any<CancellationToken>())
            .Returns(new JobExecution(Exec, "Succeeded",
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow));
    }

    private IRenderedComponent<AdminJobExecutionDetail> Render()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminJobExecutionDetail>(1);
            builder.AddAttribute(2, nameof(AdminJobExecutionDetail.JobName), Job);
            builder.AddAttribute(3, nameof(AdminJobExecutionDetail.ExecutionName), Exec);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminJobExecutionDetail>();
    }

    [Fact]
    public async Task Anonymous_SeesSignInNotice_NotLogs()
    {
        this.AddAuthorization().SetNotAuthorized();
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "secret-ish", JobLogSeverity.Info)], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-signin']");            // notice present
        Assert.Empty(cut.FindAll("[data-testid='exec-log-lines']")); // logs NOT rendered
        await _logs.DidNotReceive().GetExecutionLogsAsync(       // and never queried
            Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Admin_Ok_RendersLogLines()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok(
                [new JobLogLine(DateTimeOffset.UtcNow, "info: linker started", JobLogSeverity.Info)], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var lines = cut.Find("[data-testid='exec-log-lines']");
        Assert.Contains("linker started", lines.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_Failed_ShowsErrorState()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(JobLogResult.Failed());

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-error']");
    }

    [Fact]
    public async Task Admin_Empty_ShowsEmptyState_NotError()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(JobLogResult.Ok([], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-empty']");
    }

    [Fact]
    public async Task ExecutionNotFound_ShowsNotFound()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _svc.GetExecutionAsync(Job, Exec, Arg.Any<CancellationToken>()).Returns((JobExecution?)null);

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-not-found']");
    }

    [Fact]
    public async Task Admin_Filter_NarrowsLines()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok(
            [
                new JobLogLine(DateTimeOffset.UtcNow, "info: linked Godzilla", JobLogSeverity.Info),
                new JobLogLine(DateTimeOffset.UtcNow, "info: linked Metallica", JobLogSeverity.Info),
            ], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var input = cut.Find("[data-testid='exec-log-filter'] input");
        await cut.InvokeAsync(() => input.Input("Godzilla"));

        var lines = cut.Find("[data-testid='exec-log-lines']");
        Assert.Contains("Godzilla", lines.InnerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Metallica", lines.InnerHtml, StringComparison.Ordinal);
    }
}
