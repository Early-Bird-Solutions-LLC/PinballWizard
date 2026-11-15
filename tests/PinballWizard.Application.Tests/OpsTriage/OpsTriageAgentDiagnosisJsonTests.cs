using PinballWizard.Application.OpsTriage;
using Xunit;

namespace PinballWizard.Application.Tests.OpsTriage;

public sealed class OpsTriageAgentDiagnosisJsonTests
{
    [Fact]
    public void ParseDiagnosisJson_BindsSnakeCaseFieldsFromOpsTriagePrompt()
    {
        const string json = """
            {
              "summary": "OPDB export 404",
              "likely_cause": "opdb.org /api/export retired",
              "evidence": ["HTTP 404 in job logs"],
              "remediation_reasonable": true,
              "remediation_plan": {
                "failing_test_sketch": "Add OpdbClient export URL test",
                "fix_sketch": "Point Opdb__ExportUrl at Match Play CDN",
                "risk_notes": "Do not change unrelated scrapers"
              },
              "skip_remediation_reason": null
            }
            """;

        var diagnosis = OpsTriageAgent.ParseDiagnosisJson(json);

        Assert.Equal("OPDB export 404", diagnosis.Summary);
        Assert.Equal("opdb.org /api/export retired", diagnosis.LikelyCause);
        Assert.True(diagnosis.RemediationReasonable);
        Assert.NotNull(diagnosis.RemediationPlan);
        Assert.Equal("Add OpdbClient export URL test", diagnosis.RemediationPlan.FailingTestSketch);
        Assert.Contains("HTTP 404", diagnosis.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDiagnosisJson_RejectsMissingLikelyCause()
    {
        const string json = """{"summary":"only summary","likely_cause":"","evidence":[],"remediation_reasonable":false,"remediation_plan":null,"skip_remediation_reason":"no signal"}""";

        Assert.Throws<InvalidOperationException>(() => OpsTriageAgent.ParseDiagnosisJson(json));
    }
}
