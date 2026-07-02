using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

// Pure KQL builders + mappings. No SDK dependency — fully unit-tested.
// Queries are consumed with a QueryTimeRange(TimeSpan) so they carry no
// time filter themselves.
internal static class MonitoringKql
{
    public static TimeSpan ToTimeSpan(MonitoringWindow window) => window switch
    {
        MonitoringWindow.OneHour => TimeSpan.FromHours(1),
        MonitoringWindow.TwentyFourHours => TimeSpan.FromHours(24),
        MonitoringWindow.SevenDays => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(24),
    };

    public const string LatencyP95 =
        "customMetrics | where name == 'pinwiz.ai.duration_ms' " +
        "| summarize p95 = percentile(value, 95)";

    public const string AnsweredCount =
        "customMetrics | where name == 'pinwiz.ai.duration_ms' " +
        "| summarize answered = sum(valueCount)";

    public const string RefusalTotal =
        "customMetrics | where name == 'pinwiz.ai.refusals' " +
        "| summarize refusals = sum(value)";

    public const string RefusalByCategory =
        "customMetrics | where name == 'pinwiz.ai.refusals' " +
        "| extend cat = tostring(customDimensions.refusal_category) " +
        "| summarize c = sum(value) by cat";

    public static string FivexxRate(string pathPrefix) =>
        // Escape single quotes in pathPrefix so an operator-supplied value containing '
        // cannot close the KQL string literal early (KQL injection mitigation).
        // Per KQL docs: inside a single-quoted literal, escape ' with \'.
        // Source: https://learn.microsoft.com/en-us/kusto/query/scalar-data-types/string
        $"requests | where url has '{EscapeKqlStringLiteral(pathPrefix)}' " +
        "| summarize failed = countif(toint(resultCode) >= 500), total = count() " +
        "| extend pct = iff(total > 0, 100.0 * failed / total, 0.0) | project pct";

    // Escapes a value for safe embedding inside a KQL single-quoted string literal.
    // Backslashes are escaped first (\→\\), then single quotes ('→\'),
    // per the KQL string-literal specification.
    // Order matters: escaping quotes first would leave a backslash-before-quote
    // sequence (\') that a second pass could corrupt, and — critically —
    // an input such as a\'b would produce a\\'b in the output,
    // where KQL reads \\ as a literal backslash and the following '
    // closes the string literal, re-enabling injection.
    private static string EscapeKqlStringLiteral(string value) =>
        value.Replace(@"\", @"\\").Replace("'", @"\'");

    public const string LeaseLag =
        "customMetrics | where name == 'pinwiz.rag.changefeed_lease_lag' " +
        "| top 1 by timestamp desc | project value";

    public const string DeadLetters =
        "customMetrics | where name == 'pinwiz.rag.changefeed_dead_letter_total' " +
        "| summarize v = sum(value)";

    public const string ShortCircuits =
        "customMetrics | where name == 'pinwiz.rag.changefeed_short_circuit_total' " +
        "| summarize v = sum(value)";

    public const string ReconcileDrift =
        "customMetrics | where name == 'pinwiz.rag.changefeed_reconcile_drift_total' " +
        "| summarize v = sum(value)";

    public static IReadOnlyList<RefusalCategoryCount> NormalizeCategories(
        IEnumerable<KeyValuePair<string, long>> raw)
    {
        var lookup = raw
            .GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.Ordinal);

        return RefusalCategories.All
            .Select(cat => new RefusalCategoryCount(
                cat, lookup.TryGetValue(cat, out var c) ? c : 0))
            .ToList();
    }
}
