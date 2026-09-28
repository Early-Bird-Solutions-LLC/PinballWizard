namespace PinballWizard.Infrastructure.Tests.Scraping._TestInfra;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire immediately and move the
/// clock forward by their due time. <c>Task.Delay(wait, provider)</c> therefore
/// completes at once while <see cref="GetUtcNow"/> reflects the wait — so a test
/// can assert that the politeness gate honored a 30 s backoff without sleeping
/// 30 s. Every requested wait is recorded in <see cref="Waits"/>.
/// </summary>
public sealed class AutoAdvancingTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<TimeSpan> _waits = [];
    private DateTimeOffset _now;

    public AutoAdvancingTimeProvider(DateTimeOffset start) => _now = start;

    /// <summary>Every timer due time requested, in order.</summary>
    public IReadOnlyList<TimeSpan> Waits
    {
        get { lock (_lock) return [.. _waits]; }
    }

    /// <summary>Sum of <see cref="Waits"/>.</summary>
    public TimeSpan TotalWaited
    {
        get { lock (_lock) return _waits.Aggregate(TimeSpan.Zero, (a, b) => a + b); }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_lock)
            {
                _waits.Add(dueTime);
                _now += dueTime;
            }
            callback(state);
        }
        return new CompletedTimer();
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
