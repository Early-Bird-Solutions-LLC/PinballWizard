using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Degradation;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai;

// Behavior-asserting tests for AmbientDegradationContext (PR-D2).
//
// The implementation is an AsyncLocal-backed singleton per the IDegradationContext
// contract comment — same pattern as IHttpContextAccessor. These tests verify:
//
//  1. Mark / Reset semantics on a single logical call-chain.
//  2. Snapshot returns null when Mode == None (happy-path allocates nothing).
//  3. Isolation: concurrent async call-chains each see their own state.
//
// Tests use AmbientDegradationContext directly (not via the interface) because
// the static AsyncLocal is the point of the contract; using the interface would
// need a full DI container and the concrete type is sealed and testable.
public sealed class AmbientDegradationContextTests
{
    // ────────────────────────────────────────────────────────────────────
    // Mark / Reset / Mode / Detail / RetryAfterSeconds
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mode_Initially_IsNone()
    {
        // Because AsyncLocal is per-logical-execution-context, a fresh
        // instance seen from a fresh test method starts at None (null cell).
        var ctx = new AmbientDegradationContext();

        // Reset to guarantee a clean slate in case a prior test on the same
        // logical context (unlikely in xUnit async-isolated tests, but
        // defensive) left a value.
        ctx.Reset();

        Assert.Equal(DegradationMode.None, ctx.Mode);
    }

    [Fact]
    public void Mark_SetsMode_AndDetail_AndRetryAfterSeconds()
    {
        var ctx = new AmbientDegradationContext();
        ctx.Reset();

        ctx.Mark(DegradationMode.UpstreamThrottled, "Rate limited", 60);

        Assert.Equal(DegradationMode.UpstreamThrottled, ctx.Mode);
        Assert.Equal("Rate limited", ctx.Detail);
        Assert.Equal(60, ctx.RetryAfterSeconds);
    }

    [Fact]
    public void Mark_SearchUnavailable_SetsMode_WithNullDetail()
    {
        var ctx = new AmbientDegradationContext();
        ctx.Reset();

        ctx.Mark(DegradationMode.SearchUnavailable);

        Assert.Equal(DegradationMode.SearchUnavailable, ctx.Mode);
        Assert.Null(ctx.Detail);
        Assert.Null(ctx.RetryAfterSeconds);
    }

    [Fact]
    public void Mark_IsIdempotent_LastWriterWins()
    {
        // The contract comment explicitly states "last writer wins".
        // SearchCorpusTool might call Mark twice on a multi-tool call;
        // only the last mark should be visible.
        var ctx = new AmbientDegradationContext();
        ctx.Reset();

        ctx.Mark(DegradationMode.UpstreamThrottled, "first", 30);
        ctx.Mark(DegradationMode.SearchUnavailable, "second", null);

        Assert.Equal(DegradationMode.SearchUnavailable, ctx.Mode);
        Assert.Equal("second", ctx.Detail);
        Assert.Null(ctx.RetryAfterSeconds);
    }

    [Fact]
    public void Reset_ClearsMark_ModeBecomeNone()
    {
        var ctx = new AmbientDegradationContext();
        ctx.Mark(DegradationMode.SearchUnavailable, "AI Search 503", null);

        ctx.Reset();

        Assert.Equal(DegradationMode.None, ctx.Mode);
        Assert.Null(ctx.Detail);
        Assert.Null(ctx.RetryAfterSeconds);
    }

    // ────────────────────────────────────────────────────────────────────
    // Snapshot
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_WhenModeIsNone_ReturnsNull()
    {
        // ADR-0026 §9: Degradation is null on the healthy path. The happy-
        // path allocates nothing — the null check in AiRouter (Snapshot() ==
        // null → WizardAnswer.Degradation = null) is the design.
        var ctx = new AmbientDegradationContext();
        ctx.Reset();

        var snap = ctx.Snapshot();

        Assert.Null(snap);
    }

    [Fact]
    public void Snapshot_WhenMarked_ReturnsImmutableRecord_WithCorrectValues()
    {
        var ctx = new AmbientDegradationContext();
        ctx.Reset();

        ctx.Mark(DegradationMode.UpstreamThrottled, "rate-limited", 45);
        var snap = ctx.Snapshot();

        Assert.NotNull(snap);
        Assert.Equal(DegradationMode.UpstreamThrottled, snap.Mode);
        Assert.Equal("rate-limited", snap.Detail);
        Assert.Equal(45, snap.RetryAfterSeconds);
    }

    [Fact]
    public void Snapshot_AfterReset_ReturnsNull()
    {
        var ctx = new AmbientDegradationContext();
        ctx.Mark(DegradationMode.SearchUnavailable, "503", null);
        ctx.Reset();

        var snap = ctx.Snapshot();

        Assert.Null(snap);
    }

    [Fact]
    public void Snapshot_MultipleCallsReturnEqualRecords()
    {
        // Snapshot() constructs a new record each call (immutable value type).
        // Two successive snapshots of the same state must be value-equal.
        var ctx = new AmbientDegradationContext();
        ctx.Reset();
        ctx.Mark(DegradationMode.SearchUnavailable, "detail", null);

        var snap1 = ctx.Snapshot();
        var snap2 = ctx.Snapshot();

        Assert.Equal(snap1, snap2);
    }

    // ────────────────────────────────────────────────────────────────────
    // Async isolation
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncIsolation_ConcurrentCallChains_DoNotShareState()
    {
        // Spin up two concurrent Tasks that each set a different mode.
        // Neither should see the other's state. This is the core property
        // AsyncLocal<T> guarantees: a value set in one execution context
        // does NOT flow back to the parent or sideways to sibling Tasks.
        //
        // Implementation note: AsyncLocal<T> flows values *downward* into
        // child Tasks (Task.Run captures the current execution context).
        // To test isolation we need Tasks that start with a clean cell, so
        // we call Reset() inside each Task before marking, guaranteeing each
        // chain starts at None regardless of parent state.

        var ctx = new AmbientDegradationContext();

        // Barrier to force both tasks to reach their Mark() at the same time.
        using var barrier = new SemaphoreSlim(0, 2);
        using var releaseBarrier = new SemaphoreSlim(0, 2);

        DegradationMode? modeSeenByTask1 = null;
        DegradationMode? modeSeenByTask2 = null;

        var task1 = Task.Run(async () =>
        {
            ctx.Reset();
            ctx.Mark(DegradationMode.SearchUnavailable, "task1", null);
            barrier.Release();
            // Wait until task2 has also marked its state
            await releaseBarrier.WaitAsync();
            modeSeenByTask1 = ctx.Mode;
        });

        var task2 = Task.Run(async () =>
        {
            ctx.Reset();
            ctx.Mark(DegradationMode.UpstreamThrottled, "task2", 30);
            barrier.Release();
            await releaseBarrier.WaitAsync();
            modeSeenByTask2 = ctx.Mode;
        });

        // Wait for both tasks to have marked and be at the barrier
        await barrier.WaitAsync();
        await barrier.WaitAsync();

        // Now release both simultaneously
        releaseBarrier.Release(2);

        await Task.WhenAll(task1, task2);

        // Each Task must see its own mark, not the other's
        Assert.Equal(DegradationMode.SearchUnavailable, modeSeenByTask1);
        Assert.Equal(DegradationMode.UpstreamThrottled, modeSeenByTask2);
    }
}
