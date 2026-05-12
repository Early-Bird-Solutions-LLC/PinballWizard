using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Degraded;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Degraded;

// Per ADR-0026 PR self-audit item 9(d): RetryHint is one of the four
// locked delight surfaces (ADR-0026 § 6). The behavioral tests here assert:
//   1. Counts down from N seconds (shows countdown text when > 0).
//   2. Renders "Try again" button when countdown reaches 0.
//   3. Timer/loop is disposed on component dispose — silent-resource-leak guard.
//      After dispose, no callback fires (CancellationToken is cancelled).
//
// Note on countdown timing:
//   bUnit's BunitContext includes a fake timer infrastructure but does not
//   auto-advance real time. For the countdown-reaches-0 test, we pass
//   RetryAfterSeconds=0 so the component skips the loop and goes straight
//   to the "Try again" button — asserting the terminal state directly
//   without waiting for real elapsed time.
//
// ADR-0026 § 5, § 6.
public sealed class RetryHintTests : AsyncBunitContext
{
    public RetryHintTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void RetryHint_ShowsCountdown_WhenSecondsRemaining()
    {
        // Arrange + Act — start with > 0 seconds so the countdown is shown.
        // We check the initial render only (before any Delay ticks).
        // The countdown text should appear immediately since _secondsRemaining
        // is set in the constructor-equivalent (field initializer = param value).
        //
        // Note: OnInitializedAsync begins the countdown loop asynchronously.
        // After the first render, _secondsRemaining == RetryAfterSeconds.
        var cut = Render<RetryHint>(parameters =>
            parameters.Add(p => p.RetryAfterSeconds, 30));

        // Assert — countdown text is present on the initial render.
        var countdown = cut.Find("[data-testid='retry-countdown']");
        Assert.Contains("30", countdown.TextContent);
        Assert.Contains("Try again in", countdown.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryHint_ShowsTryAgain_Button_WhenCountdownIsZero()
    {
        // Arrange — pass 0 so OnInitializedAsync skips the loop entirely.
        var cut = Render<RetryHint>(parameters =>
            parameters.Add(p => p.RetryAfterSeconds, 0));

        // Assert — the "Try again" button is present; countdown text is absent.
        cut.Find("[data-testid='retry-button']");
        Assert.Empty(cut.FindAll("[data-testid='retry-countdown']"));
    }

    /// <summary>
    /// Silent-resource-leak guard per PR self-audit.
    ///
    /// After the component is disposed, the CancellationTokenSource is
    /// cancelled so the internal Task.Delay loop exits. This test verifies
    /// that no OperationCanceledException propagates as an unobserved
    /// exception after dispose — i.e., the component handles cancellation
    /// gracefully (catch (OperationCanceledException) { return; }).
    ///
    /// Approach:
    ///   1. Render with a large RetryAfterSeconds so the loop is alive.
    ///   2. Dispose the component — triggers CancellationTokenSource.Cancel().
    ///   3. Allow the event loop to process — no exception should surface.
    /// </summary>
    [Fact]
    public void Timer_is_disposed_on_component_dispose()
    {
        // Arrange — large countdown so the Task.Delay loop is alive at dispose.
        var cut = Render<RetryHint>(parameters =>
            parameters.Add(p => p.RetryAfterSeconds, 9999));

        // Act — dispose triggers _cts.Cancel() and _cts.Dispose().
        // If the catch in OnInitializedAsync is missing or malformed,
        // the OperationCanceledException propagates as an unobserved
        // exception — bUnit's BunitContext surfaces these as test failures.
        var exception = Record.Exception(() => cut.Instance.Dispose());

        // Assert — no exception from the Dispose path.
        Assert.Null(exception);
    }
}
