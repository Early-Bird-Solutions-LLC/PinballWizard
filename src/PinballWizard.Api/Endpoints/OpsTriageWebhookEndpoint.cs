using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Api.Endpoints;

/// <summary>
/// Azure Monitor action-group webhook ingress for Ops Triage (ADR-0064).
/// Auth: shared secret query <c>code</c> compared to <see cref="OpsTriageOptions.WebhookSecret"/>.
/// Admin reads go through Blazor + <c>ITriageRunRepository</c> (no anonymous HTTP list/get).
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

        // Bound untrusted Monitor payloads (adversary review / AGENT-08).
        const int maxBodyBytes = 256 * 1024;
        if (request.ContentLength is > maxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        string raw;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (raw.Length > maxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
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
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Azure Monitor alert payload.");
            return Results.BadRequest(new { error = "Unrecognized alert payload." });
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Failed to parse Azure Monitor alert payload.");
            return Results.BadRequest(new { error = "Unrecognized alert payload." });
        }

        var existing = await runs.GetAsync(run.Id, run.AlertDay, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Idempotent: Monitor retries must not clobber Completed/Failed or re-enqueue.
            logger.LogInformation(
                "Ops triage run {TriageRunId} already exists status={Status} — accepting without re-queue.",
                existing.Id,
                existing.Status);
            return Results.Accepted(
                $"/admin/ops-triage/{existing.Id}?day={Uri.EscapeDataString(existing.AlertDay)}",
                new { id = existing.Id, alertDay = existing.AlertDay, status = existing.Status.ToString(), deduped = true });
        }

        await runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        await queue.EnqueueAsync(run.Id, run.AlertDay, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Accepted ops triage run {TriageRunId} rule={Rule} job={Job}",
            run.Id,
            run.AlertRuleName,
            run.JobName);

        return Results.Accepted(
            $"/admin/ops-triage/{run.Id}?day={Uri.EscapeDataString(run.AlertDay)}",
            new { id = run.Id, alertDay = run.AlertDay, status = run.Status.ToString(), deduped = false });
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
