namespace PinballWizard.Application.Jobs;

// Seeded cadence catalog for --check-job-schedules (ADR-0068 / #856).
//
// Manual-only jobs (relink-all, gc-rag-index) and the paused Kineticist
// weekly sync are intentionally absent — silence there is not a schedule
// failure. The watchdog job itself (pinwiz-job-watchdog) is also absent
// (would recurse).
//
// MaxGapHours:
//   Daily   25  — 24 h cadence + 1 h jitter
//   Weekly 192  — 7 d + 1 d grace
//   Monthly 768 — 31 d + 1 d grace (ARM history, not Log Analytics window)
public static class ExpectedJobSchedules
{
    public const int DailyMaxGapHours = 25;
    public const int WeeklyMaxGapHours = 192;
    public const int MonthlyMaxGapHours = 768;

    public static IReadOnlyList<ExpectedJobSchedule> All { get; } =
    [
        // Daily
        new("pinwiz-job-linker", DailyMaxGapHours),
        new("pinwiz-job-stern-manuals", DailyMaxGapHours),
        new("pinwiz-job-stern-games", DailyMaxGapHours),
        new("pinwiz-job-stern-bulletins", DailyMaxGapHours),
        new("pinwiz-job-jjp", DailyMaxGapHours),
        new("pinwiz-job-ap", DailyMaxGapHours),

        // Weekly
        new("pinwiz-job-opdb", WeeklyMaxGapHours),
        new("pinwiz-job-stern-refresh", WeeklyMaxGapHours),
        new("pinwiz-job-twip", WeeklyMaxGapHours),
        new("pinwiz-job-pintips", WeeklyMaxGapHours),
        new("pinwiz-job-multimorphic", WeeklyMaxGapHours),
        new("pinwiz-job-cgc", WeeklyMaxGapHours),
        new("pinwiz-job-jjp-support", WeeklyMaxGapHours),
        new("pinwiz-job-ap-bulletins", WeeklyMaxGapHours),
        new("pinwiz-job-spooky", WeeklyMaxGapHours),
        new("pinwiz-job-spooky-support", WeeklyMaxGapHours),
        new("pinwiz-job-pb", WeeklyMaxGapHours),
        new("pinwiz-job-pb-docs", WeeklyMaxGapHours),
        new("pinwiz-job-pb-freshdesk", WeeklyMaxGapHours),

        // Monthly (included because the watchdog reads ARM execution history,
        // which is not clamped by Azure Monitor's 48 h log-alert window).
        new("pinwiz-job-barrelsoffun", MonthlyMaxGapHours),
    ];
}
