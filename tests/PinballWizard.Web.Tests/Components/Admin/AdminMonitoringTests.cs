using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Monitoring;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminMonitoring.razor (/admin/monitoring).
//
// Interactive (@rendermode InteractiveServer, ADR-0034 amendment). All four live
// metrics tiles (latency, 5xx, refusal, ingestion) are driven by IMonitoringStatsReader
// injected via NSubstitute. Tests cover:
//   - Live values render from the snapshot (data-driven)
//   - Null metrics render a visible "unavailable" marker, never 0 (Invariant #17)
//   - Section isolation — a null metric in one section does not blank another
//   - Window toggle re-queries with the selected MonitoringWindow
//   - Semantic invariants: cost tile Eval-only, cost value ≠ $0.00,
//     reconcile-drift canary class, D4 cost alert Suppressed (these stay static)
public sealed class AdminMonitoringTests : AsyncBunitContext
{
    private IMonitoringStatsReader _reader = default!;

    public AdminMonitoringTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddSingleton<ILogger<AdminMonitoring>>(NullLogger<AdminMonitoring>.Instance);
    }

    // ── Snapshot factory ─────────────────────────────────────────────────────

    private static MonitoringSnapshot FullSnap() => new()
    {
        Window = MonitoringWindow.TwentyFourHours,
        GeneratedAt = DateTimeOffset.UnixEpoch,
        LatencyP95Ms = 2310,
        FivexxRatePercent = 0.4,
        RefusalRatePercent = 6.2,
        RefusalCount = 103,
        AnsweredCount = 1652,
        RefusalBreakdown =
        [
            new("OutOfScope", 47), new("InsufficientGrounding", 34), new("NoCitation", 12),
            new("LowModelConfidence", 9), new("HarmfulContent", 1), new("CostCeilingHit", 0),
        ],
        LeaseLag = 0, DeadLetters = 2, ShortCircuits = 1, ReconcileDrift = 0,
    };

    // Register a mock reader, render the page with a MudPopoverProvider sibling
    // (MudBlazor 9 bUnit requirement — see reference_mudblazor9_bunit_popover_provider),
    // and return the AdminMonitoring component.
    private IRenderedComponent<AdminMonitoring> RenderWith(MonitoringSnapshot snap)
    {
        _reader = Substitute.For<IMonitoringStatsReader>();
        _reader.GetSnapshotAsync(Arg.Any<MonitoringWindow>(), Arg.Any<CancellationToken>())
               .Returns(snap);
        Services.AddSingleton(_reader);

        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminMonitoring>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminMonitoring>();
    }

    // Flush the async OnAfterRenderAsync load (same pattern as AdminJobsTests.FlushAsync).
    private static async Task FlushLoadAsync(IRenderedComponent<AdminMonitoring> cut)
        => await cut.InvokeAsync(() => Task.CompletedTask);

    // ── Render smoke ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Renders_WithoutException()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────

    [Fact]
    public void Toolbar_IsPresent()
    {
        var cut = RenderWith(FullSnap());
        cut.Find("[data-testid='monitoring-toolbar']");
    }

    [Fact]
    public async Task TimePeriod_24hIsActive()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        var active = cut.Find("[data-testid='mon-period-24h']");
        Assert.Equal("24h", active.TextContent.Trim());
        Assert.Contains("mon-period--active", active.ClassName ?? string.Empty);
    }

    // ── D1 Ribbon structure ───────────────────────────────────────────────────

    [Fact]
    public async Task D1Ribbon_HasFourTiles()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        cut.Find("[data-testid='mon-tile-latency']");
        cut.Find("[data-testid='mon-tile-5xx']");
        cut.Find("[data-testid='mon-tile-cost']");
        cut.Find("[data-testid='mon-tile-refusal']");
    }

    // ── Latency tile — value-driven ───────────────────────────────────────────

    [Fact]
    public void LatencyTile_Shows2310ms()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
            Assert.Contains("2,310", cut.Find("[data-testid='mon-tile-latency-value']").TextContent));
    }

    [Fact]
    public void LatencyTile_RendersLiveValue()
    {
        // Uses a different latency than LatencyTile_Shows2310ms to prove the tile is data-driven,
        // not matching a hardcoded string.
        var snap = FullSnap() with { LatencyP95Ms = 1500 };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
            Assert.Contains("1,500", cut.Find("[data-testid='mon-tile-latency-value']").TextContent));
    }

    [Fact]
    public async Task LatencyTile_StateIsOk()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        cut.WaitForAssertion(() =>
        {
            var state = cut.Find("[data-testid='mon-tile-latency-state']");
            Assert.Contains("OK", state.TextContent);
        });
    }

    [Fact]
    public void LatencyTile_Unavailable_ShowsUnavailableMarker_NotZero()
    {
        var snap = FullSnap() with { LatencyP95Ms = null };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid='mon-tile-latency-value']").TextContent;
            Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0", text);
        });
    }

    // ── 5xx tile ─────────────────────────────────────────────────────────────

    [Fact]
    public void FivexxTile_Shows04Percent()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
            Assert.Contains("0.4", cut.Find("[data-testid='mon-tile-5xx-value']").TextContent));
    }

    // ── Cost tile — semantic invariants (static, not driven by snapshot) ──────

    [Fact]
    public async Task CostTile_IsEvalOnly()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        var state = cut.Find("[data-testid='mon-tile-cost-state']");
        Assert.Contains("Eval-only", state.TextContent);
    }

    [Fact]
    public async Task CostTile_ValueIsUnavailableSentinel()
    {
        // Cost OTel not yet wired (#2688) — value must render as — (not $0.00)
        // so operators can't mistake "not instrumented" for "costs nothing".
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        var value = cut.Find("[data-testid='mon-tile-cost-value']");
        Assert.DoesNotContain("$0.00", value.TextContent);
    }

    // ── Refusal tile ─────────────────────────────────────────────────────────

    [Fact]
    public void RefusalTile_Shows62Percent()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
            Assert.Contains("6.2", cut.Find("[data-testid='mon-tile-refusal-value']").TextContent));
    }

    // ── D2 Refusal category bars ──────────────────────────────────────────────

    [Fact]
    public async Task D2_HasSixRefusalCategories()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        cut.Find("[data-testid='mon-refusal-outofscope']");
        cut.Find("[data-testid='mon-refusal-insufficientgrounding']");
        cut.Find("[data-testid='mon-refusal-nocitation']");
        cut.Find("[data-testid='mon-refusal-lowmodelconfidence']");
        cut.Find("[data-testid='mon-refusal-harmfulcontent']");
        cut.Find("[data-testid='mon-refusal-costceilinghit']");
    }

    // ── D3 Ingestion pipeline ─────────────────────────────────────────────────

    [Fact]
    public async Task D3_HasFourPipelineRows()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        cut.Find("[data-testid='mon-pipeline-lease']");
        cut.Find("[data-testid='mon-pipeline-deadletter']");
        cut.Find("[data-testid='mon-pipeline-shortcircuit']");
        cut.Find("[data-testid='mon-pipeline-drift']");
    }

    [Fact]
    public void D3_DeadLetterRow_StateIsReview()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
        {
            var state = cut.Find("[data-testid='mon-pipeline-deadletter-state']");
            Assert.Equal("review", state.TextContent.Trim());
        });
    }

    [Fact]
    public async Task D3_ReconcileDriftRow_HasCanaryClass()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        var driftRow = cut.Find("[data-testid='mon-pipeline-drift']");
        Assert.Contains("mon-pipeline__row--canary", driftRow.ClassName);
    }

    // ── Section isolation — null in one tile must not blank another ───────────

    [Fact]
    public void RefusalTile_Unavailable_DoesNotBlank_IngestionTiles()
    {
        // Latency null must not blank dead-letters (section isolation, Invariant #17).
        var snap = FullSnap() with { LatencyP95Ms = null };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
            Assert.Contains("2", cut.Find("[data-testid='mon-pipeline-deadletter']").TextContent));
    }

    // ── Window toggle ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WindowToggle_RequeriesWithSelectedWindow()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        // Click the 7d button inside the dispatcher (repo bUnit click convention).
        await cut.InvokeAsync(() => cut.Find("[data-testid='mon-period-7d']").Click());
        cut.WaitForAssertion(() =>
            _reader.Received().GetSnapshotAsync(MonitoringWindow.SevenDays, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task Load_FailedThenToggleSucceeds_ClearsErrorAlert()
    {
        // Fix 1: _loadFailed must reset to false at the start of every LoadAsync call so a
        // successful re-query after a prior failure clears the error alert (Invariant #17 — never
        // fabricate state: showing both the error alert AND live values is a lie).
        var snap = FullSnap();
        _reader = Substitute.For<IMonitoringStatsReader>();
        _reader.GetSnapshotAsync(Arg.Any<MonitoringWindow>(), Arg.Any<CancellationToken>())
               .Returns(
                   _ => Task.FromException<MonitoringSnapshot>(new InvalidOperationException("simulated reader failure")),
                   _ => Task.FromResult(snap));
        Services.AddSingleton(_reader);

        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminMonitoring>(1);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<AdminMonitoring>();

        // First load (OnAfterRenderAsync firstRender) throws → error alert must appear.
        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid='mon-load-failed']")));

        // Toggle window → second load succeeds → error alert must be GONE, live value visible.
        await cut.InvokeAsync(() => cut.Find("[data-testid='mon-period-7d']").Click());
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid='mon-load-failed']"));
            Assert.Contains("2,310", cut.Find("[data-testid='mon-tile-latency-value']").TextContent);
        });
    }

    // ── D1 ALERT state — distinct red CSS class ──────────────────────────────

    [Fact]
    public void LatencyTile_AlertState_HasAlertClass()
    {
        // LatencyP95Ms = 6000 > 5,000 ms threshold → ALERT state.
        // State pill must carry mon-state--alert (danger-red via --mon-status-critical),
        // tile must carry mon-tile--alert. Neither --warn nor --eval (amber).
        var snap = FullSnap() with { LatencyP95Ms = 6000 };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
        {
            var pill = cut.Find("[data-testid='mon-tile-latency-state']");
            Assert.Contains("ALERT", pill.TextContent);
            Assert.Contains("mon-state--alert", pill.ClassName ?? string.Empty);
            var tile = cut.Find("[data-testid='mon-tile-latency']");
            Assert.Contains("mon-tile--alert", tile.ClassName ?? string.Empty);
        });
    }

    [Fact]
    public void FivexxTile_AlertState_HasAlertClass()
    {
        // FivexxRatePercent = 6.0 > 5% threshold → ALERT state.
        // State pill must carry mon-state--alert (danger-red via --mon-status-critical),
        // tile must carry mon-tile--alert. Neither --warn nor --eval (amber).
        var snap = FullSnap() with { FivexxRatePercent = 6.0 };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
        {
            var pill = cut.Find("[data-testid='mon-tile-5xx-state']");
            Assert.Contains("ALERT", pill.TextContent);
            Assert.Contains("mon-state--alert", pill.ClassName ?? string.Empty);
            var tile = cut.Find("[data-testid='mon-tile-5xx']");
            Assert.Contains("mon-tile--alert", tile.ClassName ?? string.Empty);
        });
    }

    // ── D4 Alert rules — configured panel title + live now-values (Task 7) ──────

    [Fact]
    public void D4_Panel_IsTitledConfiguredAlertRules()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
            Assert.Contains("Configured alert rules",
                cut.Find("[data-testid='mon-d4-alerts']").TextContent));
    }

    [Fact]
    public void D4_LatencyAlert_ShowsLiveNowValue()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
            Assert.Contains("2,310",
                cut.Find("[data-testid='mon-alert-latency']").TextContent));
    }

    [Fact]
    public void D4_LatencyAlert_LiveValue_IsDataDriven()
    {
        // Proves the latency "now" is driven by the snapshot, not a hardcoded string.
        var snap = FullSnap() with { LatencyP95Ms = 1750 };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
            Assert.Contains("1,750",
                cut.Find("[data-testid='mon-alert-latency']").TextContent));
    }

    [Fact]
    public void D4_LatencyAlert_NullSnapshot_ShowsUnavailable()
    {
        var snap = FullSnap() with { LatencyP95Ms = null };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid='mon-alert-latency']").TextContent;
            Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void D4_FivexxAlert_NullSnapshot_ShowsUnavailable()
    {
        var snap = FullSnap() with { FivexxRatePercent = null };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid='mon-alert-5xx']").TextContent;
            Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void D4_DeadLetterAlert_NullSnapshot_ShowsUnavailable()
    {
        var snap = FullSnap() with { DeadLetters = null };
        var cut = RenderWith(snap);
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid='mon-alert-deadletter']").TextContent;
            Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void D4_CostAlert_StaysSuppressed()
    {
        var cut = RenderWith(FullSnap());
        cut.WaitForAssertion(() =>
            Assert.Contains("Suppressed",
                cut.Find("[data-testid='mon-alert-cost-state']").TextContent));
    }

    [Fact]
    public async Task D4_HasFiveAlertRows()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        cut.Find("[data-testid='mon-alert-latency']");
        cut.Find("[data-testid='mon-alert-5xx']");
        cut.Find("[data-testid='mon-alert-cost']");
        cut.Find("[data-testid='mon-alert-deadletter']");
        cut.Find("[data-testid='mon-alert-availability']");
    }

    [Fact]
    public async Task D4_DailyCostAlert_IsSuppressed()
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        var state = cut.Find("[data-testid='mon-alert-cost-state']");
        Assert.Equal("Suppressed", state.TextContent.Trim());
    }

    [Theory]
    [InlineData("mon-alert-latency-state")]
    [InlineData("mon-alert-5xx-state")]
    [InlineData("mon-alert-deadletter-state")]
    [InlineData("mon-alert-availability-state")]
    public async Task D4_OtherAlerts_AreOk(string testid)
    {
        var cut = RenderWith(FullSnap());
        await FlushLoadAsync(cut);
        var el = cut.Find($"[data-testid='{testid}']");
        Assert.Equal("OK", el.TextContent.Trim());
    }

    // ── Loading state — skeleton assertions ───────────────────────────────────
    // Renders with a blocking reader so _loading stays true while we inspect.
    // Verifies the three live tiles and three pipeline rows show MudSkeleton
    // instead of the static "…" placeholder, and that the two genuinely-final
    // sections (Daily AI Cost, Short-circuits) do NOT show a skeleton.

    private IRenderedComponent<AdminMonitoring> RenderInLoadingState()
    {
        var blockingReader = Substitute.For<IMonitoringStatsReader>();
        blockingReader.GetSnapshotAsync(Arg.Any<MonitoringWindow>(), Arg.Any<CancellationToken>())
                      .Returns(async callInfo =>
                      {
                          await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>());
                          return new MonitoringSnapshot
                          {
                              Window = MonitoringWindow.TwentyFourHours,
                              GeneratedAt = DateTimeOffset.UtcNow,
                          };
                      });
        Services.AddSingleton(blockingReader);

        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminMonitoring>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminMonitoring>();
    }

    [Theory]
    [InlineData("mon-tile-latency-value")]
    [InlineData("mon-tile-5xx-value")]
    [InlineData("mon-tile-refusal-value")]
    public void LoadingState_LiveTile_RendersMudSkeleton(string tileValueTestId)
    {
        var cut = RenderInLoadingState();
        var region = cut.Find($"[data-testid='{tileValueTestId}']");
        Assert.NotEmpty(region.QuerySelectorAll(".mud-skeleton"));
    }

    [Theory]
    [InlineData("mon-pipeline-lease")]
    [InlineData("mon-pipeline-deadletter")]
    [InlineData("mon-pipeline-drift")]
    public void LoadingState_PipelineRow_RendersMudSkeleton(string rowTestId)
    {
        var cut = RenderInLoadingState();
        var region = cut.Find($"[data-testid='{rowTestId}']");
        Assert.NotEmpty(region.QuerySelectorAll(".mud-skeleton"));
    }

    [Fact]
    public void LoadingState_CostTile_DoesNotRenderSkeleton()
    {
        // Daily AI Cost is eval-only and always shows —; it is not a loading state.
        var cut = RenderInLoadingState();
        var costValue = cut.Find("[data-testid='mon-tile-cost-value']");
        Assert.Empty(costValue.QuerySelectorAll(".mud-skeleton"));
        // Confirm the em-dash sentinel is still present (not a zero or blank).
        Assert.Contains("—", costValue.TextContent);
    }

    [Fact]
    public void LoadingState_ShortCircuitsRow_DoesNotRenderSkeleton()
    {
        // Short-circuits "expected" is an intentional final state label; no skeleton.
        var cut = RenderInLoadingState();
        var row = cut.Find("[data-testid='mon-pipeline-shortcircuit']");
        Assert.Empty(row.QuerySelectorAll(".mud-skeleton"));
    }
}

// Dispose-mid-load guard (#615): disposing AdminMonitoring while OnAfterRenderAsync is
// blocked on GetSnapshotAsync must not throw ObjectDisposedException.
//
// How the guard works:
//   1. AdminPageBase.Dispose() sets _disposed=true THEN cancels the disposal CTS.
//   2. CreateLoadCts() links each 30-second timeout CTS to the disposal CTS, so
//      the in-flight Task.Delay(Infinity, ct) sees its token cancelled and throws
//      OperationCanceledException.
//   3. The catch block runs; the finally calls SafeStateHasChanged() which sees
//      _disposed=true and returns without touching the disposed renderer.
//      Without SafeStateHasChanged(), plain StateHasChanged() on a disposed renderer
//      would throw ObjectDisposedException.
//
// Synchronisation note: the Task.Delay continuation runs on a background thread
// after DisposeAsync() returns, so we cannot await it in-test. The safety of the
// async path is verified by the synchronous AdminPageBaseTests (CreateLoadCts_CancelsOnDispose
// + SafeStateHasChanged_AfterDispose_DoesNotThrow). This test validates the integration:
// DisposeAsync() itself completes without exception when a blocking load is in progress.
public sealed class AdminMonitoringDisposeGuardTests : AsyncBunitContext
{
    public AdminMonitoringDisposeGuardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddSingleton<ILogger<AdminMonitoring>>(NullLogger<AdminMonitoring>.Instance);
    }

    [Fact]
    public async Task DisposesMidLoad_DoesNotThrowObjectDisposedException()
    {
        // Mock that blocks until the cancellation token fires (simulates a slow read).
        var reader = Substitute.For<IMonitoringStatsReader>();
        reader.GetSnapshotAsync(Arg.Any<MonitoringWindow>(), Arg.Any<CancellationToken>())
              .Returns(async callInfo =>
              {
                  await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>());
                  return new MonitoringSnapshot { Window = MonitoringWindow.TwentyFourHours, GeneratedAt = DateTimeOffset.UtcNow }; // unreachable
              });
        Services.AddSingleton(reader);

        Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminMonitoring>(1);
            builder.CloseComponent();
        });

        // OnAfterRenderAsync started and is blocked on GetSnapshotAsync.
        // DisposeAsync() → AdminPageBase.Dispose(): _disposed=true, cancels disposal CTS
        // → linked CTS from CreateLoadCts is cancelled → Task.Delay throws OCE
        // → catch block sets _loadFailed; finally: SafeStateHasChanged() returns (noop).
        await DisposeAsync(); // must not throw
    }
}
