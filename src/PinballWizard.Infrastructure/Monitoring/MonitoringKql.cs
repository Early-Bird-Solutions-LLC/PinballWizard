using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

// Pure KQL builders + mappings. No SDK dependency — fully unit-tested.
// Queries are consumed with a QueryTimeRange(TimeSpan) so they carry no
// time filter themselves.
//
// SCHEMA: these run against the Log Analytics WORKSPACE (QueryWorkspaceAsync on
// customerId 97c11e34-…), NOT the Application Insights resource endpoint. Those two
// expose different schemas for the same data, and only the workspace one is valid here:
//
//   classic (api.applicationinsights.io)   workspace (api.loganalytics.io)  <-- ours
//   ------------------------------------   -------------------------------
//   customMetrics                          AppMetrics
//     name                                   Name
//     value                                  Sum
//     valueCount                             ItemCount
//     customDimensions                       Properties
//     timestamp                              TimeGenerated
//   requests                                AppRequests
//     url                                     Url
//     resultCode                              ResultCode
//
// Using a classic name here does NOT return empty — the service rejects the request
// outright with BadArgumentError / SemanticError SEM0100 ("Failed to resolve table or
// column expression named 'customMetrics'"), surfacing as
// Azure.RequestFailedException "The request had some invalid properties". That was
// issue #851: every tile on /admin/monitoring failed on every load, and the page's
// 30s cache is not populated by failed snapshots, so it re-fired continuously.
//
// Every query below was executed against the live workspace on 2026-08-15 and returned
// 200; the pre-fix classic forms were re-run as negative controls and reproduced the
// production error exactly. Re-verify the same way before changing any table or column
// name here — a wrong one fails loudly at runtime but is invisible to a unit test that
// only string-matches the query it is asserting.
internal static class MonitoringKql
{
    public static TimeSpan ToTimeSpan(MonitoringWindow window) => window switch
    {
        MonitoringWindow.OneHour => TimeSpan.FromHours(1),
        MonitoringWindow.TwentyFourHours => TimeSpan.FromHours(24),
        MonitoringWindow.SevenDays => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(24),
    };

    // NOTE: 'pinwiz.ai.duration_ms' is not currently emitted by any host — a 7-day
    // workspace sweep on 2026-08-15 found 24 distinct pinwiz.* metrics, and this was
    // not among them (pinwiz.ai.first_token_ms and pinwiz.ai.refusals were). So these
    // two queries are CORRECT but return no rows, and the tiles render "unavailable"
    // rather than a fabricated 0 — which is the honest outcome per invariant #17.
    // The missing instrument is tracked separately; fixing the schema here does not
    // fix that, and the two defects were previously indistinguishable because the
    // request failed before it could return anything.
    public const string LatencyP95 =
        "AppMetrics | where Name == 'pinwiz.ai.duration_ms' " +
        "| summarize p95 = percentile(Sum, 95)";

    public const string AnsweredCount =
        "AppMetrics | where Name == 'pinwiz.ai.duration_ms' " +
        "| summarize answered = sum(ItemCount)";

    public const string RefusalTotal =
        "AppMetrics | where Name == 'pinwiz.ai.refusals' " +
        "| summarize refusals = sum(Sum)";

    public const string RefusalByCategory =
        "AppMetrics | where Name == 'pinwiz.ai.refusals' " +
        "| extend cat = tostring(Properties.refusal_category) " +
        "| summarize c = sum(Sum) by cat";

    public static string FivexxRate(string pathPrefix) =>
        // Escape single quotes in pathPrefix so an operator-supplied value containing '
        // cannot close the KQL string literal early (KQL injection mitigation).
        // Per KQL docs: inside a single-quoted literal, escape ' with \'.
        // Source: https://learn.microsoft.com/en-us/kusto/query/scalar-data-types/string
        $"AppRequests | where Url has '{EscapeKqlStringLiteral(pathPrefix)}' " +
        "| summarize failed = countif(toint(ResultCode) >= 500), total = count() " +
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
        "AppMetrics | where Name == 'pinwiz.rag.changefeed_lease_lag' " +
        "| top 1 by TimeGenerated desc | project Sum";

    public const string DeadLetters =
        "AppMetrics | where Name == 'pinwiz.rag.changefeed_dead_letter_total' " +
        "| summarize v = sum(Sum)";

    public const string ShortCircuits =
        "AppMetrics | where Name == 'pinwiz.rag.changefeed_short_circuit_total' " +
        "| summarize v = sum(Sum)";

    public const string ReconcileDrift =
        "AppMetrics | where Name == 'pinwiz.rag.changefeed_reconcile_drift_total' " +
        "| summarize v = sum(Sum)";

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
