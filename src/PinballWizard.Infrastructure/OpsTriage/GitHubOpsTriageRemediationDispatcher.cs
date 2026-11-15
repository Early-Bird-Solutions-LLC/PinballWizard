using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.OpsTriage;

/// <summary>
/// Dispatches <c>ops-triage-remediate.yml</c> via GitHub Actions workflow_dispatch
/// (ADR-0051 CI mutation path). Requires <see cref="OpsTriageOptions.GitHubToken"/>.
/// </summary>
public sealed class GitHubOpsTriageRemediationDispatcher : IOpsTriageRemediationDispatcher
{
    private readonly OpsTriageOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubOpsTriageRemediationDispatcher> _logger;

    public GitHubOpsTriageRemediationDispatcher(
        IOptions<OpsTriageOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubOpsTriageRemediationDispatcher> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> DispatchAsync(
        TriageRun run,
        OpsTriageDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(diagnosis);

        if (string.IsNullOrWhiteSpace(_options.GitHubToken))
        {
            _logger.LogWarning(
                "Ops triage remediation skipped for {TriageRunId} — GitHub token not configured.",
                run.Id);
            return null;
        }

        var parts = _options.GitHubRepository.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException(
                $"OpsTriage:GitHubRepository must be owner/repo; got '{_options.GitHubRepository}'.");
        }

        var (owner, repo) = (parts[0], parts[1]);
        var client = _httpClientFactory.CreateClient(nameof(GitHubOpsTriageRemediationDispatcher));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHubToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PinballWizard-OpsTriage");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var payload = new
        {
            @ref = "main",
            inputs = new Dictionary<string, string>
            {
                ["triage_run_id"] = run.Id,
                ["alert_day"] = run.AlertDay,
                ["alert_rule"] = run.AlertRuleName,
                ["job_name"] = run.JobName ?? string.Empty,
                ["diagnosis_summary"] = diagnosis.Summary,
                ["likely_cause"] = diagnosis.LikelyCause,
                ["failing_test_sketch"] = diagnosis.RemediationPlan?.FailingTestSketch ?? string.Empty,
                ["fix_sketch"] = diagnosis.RemediationPlan?.FixSketch ?? string.Empty,
                ["risk_notes"] = diagnosis.RemediationPlan?.RiskNotes ?? string.Empty,
            },
        };

        var url = $"https://api.github.com/repos/{owner}/{repo}/actions/workflows/{_options.RemediationWorkflow}/dispatches";
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"GitHub workflow_dispatch failed: {(int)response.StatusCode} {Truncate(body, 300)}");
        }

        // workflow_dispatch returns 204 with no run id; deep-link the workflow runs list.
        var runsUrl = $"https://github.com/{owner}/{repo}/actions/workflows/{_options.RemediationWorkflow}";
        _logger.LogInformation(
            "Ops triage remediation dispatched for {TriageRunId} → {RunsUrl}",
            run.Id,
            runsUrl);
        return runsUrl;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
