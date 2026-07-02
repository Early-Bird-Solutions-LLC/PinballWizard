using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// Unit tests for AdminPageBase — the dispose-guard base class (#615).
//
// Pure unit tests (no bUnit) because AdminPageBase's behavior is CancellationToken
// lifetime management, not rendering. Three behaviors under test:
//   - SafeStateHasChanged() is a no-op after Dispose()
//   - ComponentDisposalToken fires on Dispose()
//   - CreateLoadCts() produces a token that fires on Dispose()
public sealed class AdminPageBaseTests
{
    // Thin subclass that surfaces the protected AdminPageBase API for testing.
    // BuildRenderTree is required by ComponentBase (abstract method) and is a
    // no-op here since we never attach this component to a Blazor renderer.
    private sealed class TestPage : AdminPageBase
    {
        public void PublicSafeStateHasChanged() => SafeStateHasChanged();
        public CancellationToken PublicComponentDisposalToken => ComponentDisposalToken;
        public CancellationTokenSource PublicCreateLoadCts(TimeSpan timeout) => CreateLoadCts(timeout);

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) { }
    }

    // ── SafeStateHasChanged ────────────────────────────────────────────────────

    [Fact]
    public void SafeStateHasChanged_AfterDispose_DoesNotThrow()
    {
        // The core guard: _disposed=true must short-circuit before StateHasChanged()
        // is called. On a real disposed component attached to a disposed renderer,
        // calling StateHasChanged() would throw ObjectDisposedException (#615).
        var page = new TestPage();
        page.Dispose();

        page.PublicSafeStateHasChanged(); // must not throw
    }

    // ── ComponentDisposalToken ─────────────────────────────────────────────────

    [Fact]
    public void ComponentDisposalToken_IsNotCancelledBeforeDispose()
    {
        using var page = new TestPage();
        Assert.False(page.PublicComponentDisposalToken.IsCancellationRequested);
    }

    [Fact]
    public void ComponentDisposalToken_CancelsOnDispose()
    {
        var page = new TestPage();
        // Capture the token before disposal to observe the cancellation signal.
        var token = page.PublicComponentDisposalToken;

        page.Dispose();

        Assert.True(token.IsCancellationRequested);
    }

    // ── CreateLoadCts ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateLoadCts_IsNotCancelledBeforeDispose()
    {
        using var page = new TestPage();
        using var cts = page.PublicCreateLoadCts(TimeSpan.FromMinutes(5));

        Assert.False(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public void CreateLoadCts_CancelsOnDispose()
    {
        // Verifies that the linked source: disposing the component cancels any
        // in-flight CTS produced by CreateLoadCts, which cancels Task.Delay(Infinity, ct)
        // in LoadAsync — the core mechanism that unblocks a blocked load on navigation-away.
        var page = new TestPage();
        using var cts = page.PublicCreateLoadCts(TimeSpan.FromMinutes(5));

        page.Dispose();

        Assert.True(cts.Token.IsCancellationRequested);
    }
}
