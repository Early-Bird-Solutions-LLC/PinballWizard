namespace PinballWizard.Application.Jobs;

// Distinguishes "no logs" (Ok + empty) from "could not load" (Failed) and
// "not wired" (Unconfigured) so the page degrades visibly (Invariant #17).
public enum JobLogAvailability { Ok, Unconfigured, Failed }

public sealed record JobLogResult(
    JobLogAvailability Availability,
    IReadOnlyList<JobLogLine> Lines,
    bool Truncated)
{
    private static readonly IReadOnlyList<JobLogLine> Empty = [];

    public static JobLogResult Ok(IReadOnlyList<JobLogLine> lines, bool truncated) =>
        new(JobLogAvailability.Ok, lines, truncated);

    public static JobLogResult Unconfigured() => new(JobLogAvailability.Unconfigured, Empty, false);

    public static JobLogResult Failed() => new(JobLogAvailability.Failed, Empty, false);
}
