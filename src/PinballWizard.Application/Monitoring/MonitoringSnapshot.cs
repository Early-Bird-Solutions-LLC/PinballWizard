namespace PinballWizard.Application.Monitoring;

public sealed record RefusalCategoryCount(string Category, long Count);

public sealed record MonitoringSnapshot
{
    public required MonitoringWindow Window { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    // Every metric is nullable; null means "this tile's source was unavailable"
    // and MUST render a visible unavailable state (Invariant #17) — never 0.
    public double? LatencyP95Ms { get; init; }
    public double? FivexxRatePercent { get; init; }
    public double? RefusalRatePercent { get; init; }
    public long? RefusalCount { get; init; }
    public long? AnsweredCount { get; init; }
    public IReadOnlyList<RefusalCategoryCount>? RefusalBreakdown { get; init; }
    public long? LeaseLag { get; init; }
    public long? DeadLetters { get; init; }
    public long? ShortCircuits { get; init; }
    public long? ReconcileDrift { get; init; }
}

public static class RefusalCategories
{
    // Canonical order — must match AdminMonitoring.razor rows and the
    // pinwiz.ai.refusals `refusal_category` tag values.
    public static readonly IReadOnlyList<string> All =
    [
        "OutOfScope",
        "InsufficientGrounding",
        "NoCitation",
        "LowModelConfidence",
        "HarmfulContent",
        "CostCeilingHit",
    ];
}
