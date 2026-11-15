using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Observability;

namespace PinballWizard.Application.OpsTriage;

public sealed class OpsTriageAgent : IOpsTriageAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IFoundryAgentFactory _agentFactory;
    private readonly ILogger<OpsTriageAgent> _logger;

    public OpsTriageAgent(IFoundryAgentFactory agentFactory, ILogger<OpsTriageAgent> logger)
    {
        _agentFactory = agentFactory;
        _logger = logger;
    }

    public async Task<OpsTriageDiagnosis> DiagnoseAsync(string alertContextJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alertContextJson);

        var agent = _agentFactory.GetAgent(AgentName.OpsTriage);
        var prompt =
            "Diagnose this Azure Monitor / ops alert. Return ONLY the JSON object from your instructions.\n\n"
            + alertContextJson;

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = response.Text ?? string.Empty;
        var json = ExtractJson(text);

        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning(
                "OpsTriage agent returned non-JSON diagnosis: {Response}",
                LogSanitizer.ForLog(text));
            throw new InvalidOperationException("OpsTriage agent returned non-JSON diagnosis.");
        }

        var dto = JsonSerializer.Deserialize<DiagnosisDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("OpsTriage agent JSON deserialized to null.");

        if (string.IsNullOrWhiteSpace(dto.Summary) || string.IsNullOrWhiteSpace(dto.LikelyCause))
        {
            throw new InvalidOperationException("OpsTriage agent JSON missing required summary/likely_cause.");
        }

        OpsTriageRemediationPlan? plan = null;
        if (dto.RemediationReasonable && dto.RemediationPlan is not null)
        {
            plan = new OpsTriageRemediationPlan(
                dto.RemediationPlan.FailingTestSketch ?? string.Empty,
                dto.RemediationPlan.FixSketch ?? string.Empty,
                dto.RemediationPlan.RiskNotes ?? string.Empty);
        }

        return new OpsTriageDiagnosis(
            Summary: Truncate(dto.Summary.Trim(), 160),
            LikelyCause: dto.LikelyCause.Trim(),
            Evidence: dto.Evidence ?? [],
            RemediationReasonable: dto.RemediationReasonable,
            RemediationPlan: plan,
            SkipRemediationReason: dto.SkipRemediationReason);
    }

    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : string.Empty;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed class DiagnosisDto
    {
        public string? Summary { get; set; }
        public string? LikelyCause { get; set; }
        public List<string>? Evidence { get; set; }
        public bool RemediationReasonable { get; set; }
        public RemediationPlanDto? RemediationPlan { get; set; }
        public string? SkipRemediationReason { get; set; }
    }

    private sealed class RemediationPlanDto
    {
        public string? FailingTestSketch { get; set; }
        public string? FixSketch { get; set; }
        public string? RiskNotes { get; set; }
    }
}
