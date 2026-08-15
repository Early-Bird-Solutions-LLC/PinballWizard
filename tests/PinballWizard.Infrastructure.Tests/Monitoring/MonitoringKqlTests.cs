using System.Reflection;
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringKqlTests
{
    [Theory]
    [InlineData(MonitoringWindow.OneHour, 1)]
    [InlineData(MonitoringWindow.TwentyFourHours, 24)]
    [InlineData(MonitoringWindow.SevenDays, 168)]
    public void ToTimeSpan_MapsWindowToHours(MonitoringWindow w, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), MonitoringKql.ToTimeSpan(w));
    }

    [Fact]
    public void NormalizeCategories_FillsMissingWithZero_InCanonicalOrder()
    {
        var raw = new[]
        {
            new KeyValuePair<string, long>("InsufficientGrounding", 34),
            new KeyValuePair<string, long>("OutOfScope", 47),
            new KeyValuePair<string, long>("Bogus", 99), // unknown -> dropped
        };

        var result = MonitoringKql.NormalizeCategories(raw);

        Assert.Equal(RefusalCategories.All.Count, result.Count);
        Assert.Equal("OutOfScope", result[0].Category);
        Assert.Equal(47, result[0].Count);
        Assert.Equal("InsufficientGrounding", result[1].Category);
        Assert.Equal(34, result[1].Count);
        Assert.Equal("CostCeilingHit", result[5].Category);
        Assert.Equal(0, result[5].Count); // missing -> 0
        Assert.DoesNotContain(result, r => r.Category == "Bogus");
    }

    [Fact]
    public void FivexxRate_ScopesToConfiguredPathPrefix()
    {
        var kql = MonitoringKql.FivexxRate("/api/wizard/");
        Assert.Contains("/api/wizard/", kql);
        Assert.Contains("ResultCode", kql);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Schema guard (#851).
    //
    // These queries run via QueryWorkspaceAsync against the Log Analytics WORKSPACE,
    // which exposes AppMetrics/AppRequests — NOT the Application Insights resource
    // endpoint, which exposes classic customMetrics/requests. Mixing them is not a
    // soft failure that yields an empty tile: the service rejects the entire request
    // with BadArgumentError / SemanticError SEM0100, surfacing as
    // Azure.RequestFailedException "The request had some invalid properties".
    //
    // That was #851 — every /admin/monitoring tile failed on every page load for
    // weeks. It survived because the tests here asserted fragments ("/api/wizard/",
    // an escaped quote) and never asserted the schema, so the table names were free
    // to be wrong while the suite stayed green. These two tests close that gap by
    // pinning the thing that was actually broken.
    //
    // Both forms were executed against the live workspace on 2026-08-15: the
    // workspace forms returned 200, the classic forms reproduced the production
    // error exactly.
    // ─────────────────────────────────────────────────────────────────────

    // Reflection over the const fields (rather than a hand-listed set) so a query
    // added later is covered automatically instead of silently escaping the guard.
    private static IEnumerable<(string Name, string Kql)> AllQueries()
    {
        foreach (var f in typeof(MonitoringKql)
                     .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            yield return (f.Name, (string)f.GetRawConstantValue()!);
        }

        // Built by a method, so it has no const field to reflect over.
        yield return (nameof(MonitoringKql.FivexxRate), MonitoringKql.FivexxRate("/api/wizard/"));
    }

    [Fact]
    public void EveryQuery_StartsWithAWorkspaceTable()
    {
        string[] workspaceTables =
            ["AppMetrics", "AppRequests", "AppTraces", "AppDependencies", "AppExceptions"];

        var queries = AllQueries().ToList();

        // NOT Assert.NotEmpty: AllQueries() always yields FivexxRate unconditionally, so
        // a non-empty result proves only that FivexxRate still exists. If the reflection
        // filter ever stopped matching (say the consts became `static readonly`, which is
        // not IsLiteral), both schema guards would quietly shrink to checking one query
        // and still pass — the same "test that stopped testing" failure this whole file
        // exists to prevent. Assert the real count instead.
        Assert.True(
            queries.Count >= 9,
            $"AllQueries() found only {queries.Count} queries. Either the reflection over " +
            "MonitoringKql's const fields stopped matching, or a method-built query was " +
            "added without registering it in AllQueries().");

        foreach (var (name, kql) in queries)
        {
            var firstToken = kql.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.True(
                workspaceTables.Contains(firstToken, StringComparer.Ordinal),
                $"{name} queries '{firstToken}', which is not a Log Analytics workspace table. " +
                $"Expected one of: {string.Join(", ", workspaceTables)}.");
        }
    }

    [Fact]
    public void NoQuery_UsesClassicApplicationInsightsIdentifiers()
    {
        // Case-sensitive: the workspace spellings (Name, Sum, ItemCount, Properties,
        // ResultCode, Url, TimeGenerated) differ from these only by case in several
        // cases, so an ordinal comparison is the whole point.
        string[] classicIdentifiers =
        [
            "customMetrics", "customDimensions", "valueCount",
            "resultCode", "sum(value)", "percentile(value", "timestamp",
        ];

        foreach (var (name, kql) in AllQueries())
        {
            foreach (var classic in classicIdentifiers)
            {
                Assert.False(
                    kql.Contains(classic, StringComparison.Ordinal),
                    $"{name} uses the classic App Insights identifier '{classic}'. " +
                    "The workspace endpoint rejects the whole request (SEM0100) rather " +
                    "than returning empty — see #851.");
            }
        }
    }

    [Fact]
    public void FivexxRate_EscapesSingleQuote_CannotBreakOutOfLiteral()
    {
        // A pathPrefix with an embedded single quote must not close the KQL string
        // literal early. The raw quote must appear escaped (\') in the output so it
        // cannot terminate the 'has ...' predicate and append arbitrary KQL.
        const string maliciousPrefix = "/api/foo' | union requests //";
        var kql = MonitoringKql.FivexxRate(maliciousPrefix);

        // The raw, unescaped single-quote must NOT appear in the output.
        // (If it did, it would close the literal and the injection would be live.)
        Assert.DoesNotContain("'has '/api/foo'", kql);

        // The escaped form must appear instead.
        Assert.Contains(@"has '/api/foo\' | union requests //'", kql);
    }

    [Fact]
    public void FivexxRate_EscapesBackslashBeforeQuote_CannotBreakOut()
    {
        // Input: a\'b (chars: a, backslash, quote, b)
        // Without escaping backslashes first, the current Replace("'", @"\'") would produce:
        //   a\\'b  (a, backslash, backslash, quote, b)
        // KQL parses \\ as a literal backslash, then ' CLOSES the string → injection survives.
        // Correct fix: escape backslashes first (\→\\), THEN escape quotes ('→\').
        // Expected output in KQL: a\\\'b (a, \\=literal-backslash, \'=escaped-quote, b)
        // → KQL sees: a\' and the literal stays closed.
        const string input = @"a\'b"; // a, backslash, quote, b
        var kql = MonitoringKql.FivexxRate(input);

        // The escaped result must contain a\\\'b — both the backslash AND the quote escaped.
        Assert.Contains(@"'a\\\'b'", kql);
    }
}
