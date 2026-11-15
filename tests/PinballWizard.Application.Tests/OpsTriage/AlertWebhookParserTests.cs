using PinballWizard.Application.OpsTriage;
using Xunit;

namespace PinballWizard.Application.Tests.OpsTriage;

public sealed class AlertWebhookParserTests
{
    [Fact]
    public void Parse_CommonAlertSchema_ExtractsRuleAndJobDimension()
    {
        const string json = """
            {
              "schemaId": "azureMonitorCommonAlertSchema",
              "data": {
                "essentials": {
                  "alertId": "/subscriptions/x/providers/Microsoft.AlertsManagement/alerts/abc",
                  "alertRule": "pinwiz-alert-aca-job-failure",
                  "severity": "2",
                  "description": "A pinwiz-job-* Container App Job completed with condition Failed."
                },
                "alertContext": {
                  "condition": {
                    "allOf": [
                      {
                        "dimensions": [
                          { "name": "JobName_s", "value": "pinwiz-job-opdb-buutj" }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """;

        var now = new DateTimeOffset(2026, 11, 15, 21, 0, 0, TimeSpan.Zero);
        var run = AlertWebhookParser.Parse(json, now);

        Assert.Equal("pinwiz-alert-aca-job-failure", run.AlertRuleName);
        Assert.Equal("pinwiz-job-opdb-buutj", run.JobName);
        Assert.Equal("2", run.Severity);
        Assert.Equal("2026-11-15", run.AlertDay);
        Assert.Equal(TriageRunStatus.Accepted, run.Status);
        Assert.StartsWith("triage_", run.Id, StringComparison.Ordinal);
        Assert.Contains("Failed", run.AlertSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SameAlert_IsIdempotent()
    {
        const string json = """
            {
              "data": {
                "essentials": {
                  "alertId": "same-id",
                  "alertRule": "pinwiz-alert-5xx-rate",
                  "description": "5xx spike"
                }
              }
            }
            """;

        var a = AlertWebhookParser.Parse(json, DateTimeOffset.UtcNow);
        var b = AlertWebhookParser.Parse(json, DateTimeOffset.UtcNow);
        Assert.Equal(a.Id, b.Id);
    }
}
