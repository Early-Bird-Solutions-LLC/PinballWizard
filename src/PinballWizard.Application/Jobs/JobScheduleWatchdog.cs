namespace PinballWizard.Application.Jobs;

// Pure overdue evaluation for #856. Separated from ARM I/O so tests can
// assert the never-ran / manual-only / gap-crossed cases without Azure.
public static class JobScheduleWatchdog
{
    // ACA CronJob-style scheduled execution names end in a numeric id
    // (unix minutes), e.g. pinwiz-job-linker-buutj-29911800. Manual starts
    // use a random alphanumeric suffix (…-8qjv4e3). Verified live 2026-11-16.
    // Manual success must NOT reset the schedule clock (#863 / ADR-0068).
    public static bool IsScheduledExecutionName(string jobResourceName, string executionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionName);

        var prefix = jobResourceName + "-";
        if (!executionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = executionName[prefix.Length..];
        if (suffix.Length == 0)
            return false;

        foreach (var c in suffix)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    // Strip the 5-char uniqueString suffix: pinwiz-job-linker-buutj → pinwiz-job-linker.
    public static string ToBareJobName(string fullJobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullJobName);
        const string prefix = "pinwiz-job-";
        if (!fullJobName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullJobName;

        var withoutPrefix = fullJobName[prefix.Length..];
        var lastDash = withoutPrefix.LastIndexOf('-');
        if (lastDash <= 0)
            return fullJobName;

        return prefix + withoutPrefix[..lastDash];
    }

    public static IReadOnlyList<OverdueJobSchedule> FindOverdue(
        IReadOnlyList<ExpectedJobSchedule> expected,
        IReadOnlyList<JobScheduleObservation> observations,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observations);

        var byBare = observations.ToDictionary(
            o => o.BareJobName,
            o => o,
            StringComparer.OrdinalIgnoreCase);

        var overdue = new List<OverdueJobSchedule>();
        foreach (var exp in expected)
        {
            byBare.TryGetValue(exp.BareJobName, out var obs);
            var last = obs?.LastScheduledSuccessUtc;
            var elapsed = last is { } t
                ? (nowUtc - t).TotalHours
                : exp.MaxGapHours + 1;

            if (elapsed > exp.MaxGapHours)
            {
                overdue.Add(new OverdueJobSchedule(
                    BareJobName: exp.BareJobName,
                    FullJobName: obs?.FullJobName,
                    MaxGapHours: exp.MaxGapHours,
                    ElapsedHours: elapsed,
                    LastScheduledSuccessUtc: last));
            }
        }

        return overdue;
    }
}
