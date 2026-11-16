using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

// Microsoft.App/jobs names must be 2–32 chars (ContainerAppInvalidName).
// Deploy 43784597251 / #1065 failed when pinwiz-job-schedule-watchdog-*
// evaluated to 34 characters. This contract walks aca-jobs.bicep and asserts
// every jobName expression fits ≤32 after substituting a 5-char uniqueString.
public sealed class AcaJobNameLengthContractTests
{
    private const int MaxAcaJobNameLength = 32;
    private const string UniqueSuffix = "xxxxx"; // uniqueString(..., 0, 5)

    // Captures the bare prefix before -${substring(uniqueString(...), 0, 5)}.
    private static readonly Regex JobNamePrefix = new(
        @"^\s*jobName:\s*'(pinwiz-job-[a-z0-9-]+)-\$\{substring\(uniqueString\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [Fact]
    public void EveryAcaJobsBicepJobName_FitsWithin32Chars()
    {
        var path = Path.Combine(RepoRoot(), "infra", "modules", "aca-jobs.bicep");
        Assert.True(File.Exists(path), $"Missing {path}");

        var text = File.ReadAllText(path);
        var matches = JobNamePrefix.Matches(text);
        Assert.True(matches.Count > 0, "Expected at least one jobName literal in aca-jobs.bicep.");

        var offenders = new List<string>();
        foreach (Match m in matches)
        {
            var bare = m.Groups[1].Value;
            var full = $"{bare}-{UniqueSuffix}";
            if (full.Length > MaxAcaJobNameLength)
                offenders.Add($"{full} ({full.Length} > {MaxAcaJobNameLength})");
        }

        Assert.True(
            offenders.Count == 0,
            "ACA job names must be ≤32 chars including the 5-char uniqueString suffix. Offenders: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void WatchdogJobNameLiteral_UsesShortForm()
    {
        // Regression pin for the #1065 → #1070 rename (comment text may still
        // mention the rejected long name; only the jobName literal matters).
        var path = Path.Combine(RepoRoot(), "infra", "modules", "aca-jobs.bicep");
        var text = File.ReadAllText(path);
        Assert.Contains(
            "jobName: 'pinwiz-job-watchdog-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "jobName: 'pinwiz-job-schedule-watchdog-",
            text,
            StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
    }
}
