namespace PinballWizard.Application.Ai.Degradation;

// AsyncLocal-backed singleton implementation of IDegradationContext.
//
// Each logical call-chain (AiRouter.AnswerAsync / AnswerStreamingAsync)
// gets its own DegradationState cell in the AsyncLocal<> flow. Concurrent
// calls on different async contexts are isolated automatically — AsyncLocal<T>
// flows through awaits and does NOT share state across independent call-chains
// (setting a value in one async execution context does not affect sibling
// execution contexts).
//
// Thread-safety: AsyncLocal<T> reads/writes are inherently thread-safe for
// the flow semantics we rely on. The DegradationState record is immutable
// (a sealed record), so there is no torn-read risk.
//
// Naming: "Ambient" signals the AsyncLocal approach, mirroring the .NET
// naming conventions used for HttpContextAccessor (which also maintains
// per-request ambient state in a singleton service).
public sealed class AmbientDegradationContext : IDegradationContext
{
    // The AsyncLocal cell. A null value means "no degradation marked for
    // this call-chain" — equivalent to DegradationMode.None. Storing null
    // rather than a default-mode record avoids allocating a state object on
    // the happy (non-degraded) path.
    private static readonly AsyncLocal<DegradationState?> _current = new();

    public DegradationMode Mode => _current.Value?.Mode ?? DegradationMode.None;

    public string? Detail => _current.Value?.Detail;

    public int? RetryAfterSeconds => _current.Value?.RetryAfterSeconds;

    public void Mark(DegradationMode mode, string? detail = null, int? retryAfterSeconds = null)
    {
        _current.Value = new DegradationState(mode, detail, retryAfterSeconds);
    }

    public void Reset()
    {
        _current.Value = null;
    }

    public DegradationContext? Snapshot()
    {
        var state = _current.Value;
        if (state is null || state.Mode == DegradationMode.None)
        {
            return null;
        }

        return new DegradationContext(state.Mode, state.Detail, state.RetryAfterSeconds);
    }

    // Immutable per-call state cell. Sealed record for value equality
    // (useful in tests that assert state transitions) and immutability
    // (no partial-write risk when read across async awaits).
    private sealed record DegradationState(
        DegradationMode Mode,
        string? Detail,
        int? RetryAfterSeconds);
}
