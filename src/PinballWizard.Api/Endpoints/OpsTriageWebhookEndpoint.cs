using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Api.Endpoints;

/// <summary>
/// Azure Monitor action-group webhook ingress for Ops Triage (ADR-0064).
/// Auth: shared secret query <c>code</c> compared to <see cref="OpsTriageOptions.WebhookSecret"/>.
/// </summary>
public static class OpsTriageWebhookEndpoint
{
    public static IEndpointRouteBuilder MapOpsTriageWebhookEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/ops/alerts/webhook", HandleAsync)
            .WithName("OpsTriageWebhook")
            .WithTags("OpsTriage")
            .AllowAnonymous();

        endpoints.MapGet("/api/ops/triage", ListAsync)
            .WithName("OpsTriageList")
            .WithTags("OpsTriage")
            .AllowAnonymous();

        endpoints.MapGet("/api/ops/triage/{id}", GetAsync)
            .WithName("OpsTriageGet")
            .WithTags("OpsTriage")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        [FromQuery] string? code,
        IOptions<OpsTriageOptions> options,
        ITriageRunRepository runs,
        IOpsTriageWorkChannel queue,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OpsTriageWebhook");
        var opts = options.Value;

        if (!opts.Enabled)
        {
            return Results.Json(
                new { error = "Ops triage is disabled (OpsTriage:Enabled=false)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(opts.WebhookSecret)
            || string.IsNullOrWhiteSpace(code)
            || !FixedTimeEquals(opts.WebhookSecret, code))
        {
            return Results.Unauthorized();
        }

        string raw;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Results.BadRequest(new { error = "Empty alert payload." });
        }

        TriageRun run;
        try
        {
            run = AlertWebhookParser.Parse(raw, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Azure Monitor alert payload.");
            return Results.BadRequest(new { error = "Unrecognized alert payload." });
        }

        await runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        await queue.EnqueueAsync(run.Id, run.AlertDay, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Accepted ops triage run {TriageRunId} rule={Rule} job={Job}",
            run.Id,
            run.AlertRuleName,
            run.JobName);

        return Results.Accepted(
            $"/api/ops/triage/{run.Id}?day={Uri.EscapeDataString(run.AlertDay)}",
            new { id = run.Id, alertDay = run.AlertDay, status = run.Status.ToString() });
    }

    private static async Task<IResult> ListAsync(
        ITriageRunRepository? runs,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (runs is null)
        {
            return Results.Json(
                new { error = "Triage runs require Cosmos persistence." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        take = Math.Clamp(take, 1, 200);
        var items = await runs.ListRecentAsync(take, cancellationToken).ConfigureAwait(false);
        return Results.Ok(items.Select(ToDto));
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromQuery] string day,
        ITriageRunRepository? runs,
        CancellationToken cancellationToken)
    {
        if (runs is null)
        {
            return Results.Json(
                new { error = "Triage runs require Cosmos persistence." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(day))
        {
            return Results.BadRequest(new { error = "Query parameter 'day' (yyyy-MM-dd) is required." });
        }

        var run = await runs.GetAsync(id, day, cancellationToken).ConfigureAwait(false);
        return run is null ? Results.NotFound() : Results.Ok(ToDto(run));
    }

    private static object ToDto(TriageRun run) => new
    {
        run.Id,
        run.AlertDay,
        run.AlertRuleName,
        run.JobName,
        run.Severity,
        run.AlertSummary,
        status = run.Status.ToString(),
        run.DiagnosisSummary,
        run.LikelyCause,
        run.Evidence,
        run.RemediationReasonable,
        run.RemediationPlanJson,
        run.SkipRemediationReason,
        run.NotificationStatus,
        run.RemediationWorkflowRunUrl,
        run.PullRequestUrl,
        run.ErrorMessage,
        run.CreatedAtUtc,
        run.DiagnosedAtUtc,
        run.CompletedAtUtc,
    };

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
