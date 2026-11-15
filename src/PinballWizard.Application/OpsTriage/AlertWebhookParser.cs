using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PinballWizard.Application.OpsTriage;

/// <summary>Maps Azure Monitor common-alert-schema JSON into a TriageRun shell.</summary>
public static class AlertWebhookParser
{
    public static TriageRun Parse(string rawJson, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var data = root.TryGetProperty("data", out var dataEl) ? dataEl : root;
        var essentials = data.TryGetProperty("essentials", out var ess) ? ess : default;

        var ruleName = ReadString(essentials, "alertRule")
            ?? ReadString(essentials, "monitoringService")
            ?? ReadString(root, "name")
            ?? "unknown-alert";

        var severity = ReadString(essentials, "severity");
        var description = ReadString(essentials, "description") ?? ruleName;
        var alertId = ReadString(essentials, "alertId") ?? ComputeFallbackId(rawJson);

        string? jobName = null;
        if (data.TryGetProperty("alertContext", out var ctx)
            && ctx.TryGetProperty("condition", out var condition)
            && condition.TryGetProperty("allOf", out var allOf)
            && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var clause in allOf.EnumerateArray())
            {
                if (clause.TryGetProperty("dimensions", out var dims)
                    && dims.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dim in dims.EnumerateArray())
                    {
                        var name = ReadString(dim, "name");
                        if (string.Equals(name, "JobName_s", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "JobName", StringComparison.OrdinalIgnoreCase))
                        {
                            jobName = ReadString(dim, "value");
                        }
                    }
                }
            }
        }

        var day = nowUtc.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var id = "triage_" + StableHash(alertId + "|" + ruleName + "|" + (jobName ?? ""));

        return new TriageRun
        {
            Id = id,
            AlertDay = day,
            AlertRuleName = ruleName,
            JobName = jobName,
            Severity = severity,
            AlertSummary = Truncate(description, 400),
            RawPayloadJson = rawJson,
            Status = TriageRunStatus.Accepted,
            CreatedAtUtc = nowUtc,
        };
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private static string ComputeFallbackId(string rawJson) =>
        StableHash(rawJson);

    private static string StableHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
