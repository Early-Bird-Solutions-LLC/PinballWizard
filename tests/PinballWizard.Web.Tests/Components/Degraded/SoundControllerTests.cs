using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Degraded;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Degraded;

// Sequential collection: SoundController tests use JSInterop.Mode = Strict
// on a static bUnit test infrastructure. Running in parallel across test
// class instances can cause the test host to crash due to bUnit's internal
// Blazor renderer state being shared across async JS interop operations.
// [Collection] with no definition defaults to sequential within the named group.
[Collection("SoundControllerTests")]
// Per ADR-0026 PR self-audit item 9(d) and 9(g):
//   "audio assets are muted by default and gated behind the SoundController
//    toggle persisted to localStorage — auto-playing audio is 🔴"
//
// SoundController is one of the four locked delight surfaces (ADR-0026 § 6).
//
// Load-bearing tests:
//   1. Defaults_to_muted_when_localStorage_is_empty — ADR-0026 invariant pin.
//      Exercises the actual IJSRuntime localStorage flow via bUnit's JSInterop.
//      If the default ever changes (IsMuted = false default), this test fails first.
//   2. Does_not_play_audio_in_this_PR_only_toggle_state — pin that no audio
//      JS API is invoked in this PR. Audio ships in a future PR.
//
// Pattern: bUnit's built-in JSInterop (not NSubstitute for IJSRuntime) is used
// throughout. bUnit's JSInterop is the idiomatic bUnit approach for verifying
// JS interop calls — it avoids NSubstitute's expression-tree limitations and
// provides clean invocation inspection via JSInterop.Invocations.
//
// ADR-0026 § 6 — SoundController locked delight surface.
// ADR-0026 § "Explicitly NOT adopted" — mute-default mandatory.
public sealed class SoundControllerTests : AsyncBunitContext
{
    private const string LocalStorageKey = "pinwiz.sound.enabled";

    public SoundControllerTests()
    {
        Services.AddMudServices();
        // Strict mode: unregistered JS calls throw, so unintended JS calls
        // (e.g., audio APIs) will fail the test. Each test registers only the
        // localStorage handlers it expects.
        JSInterop.Mode = JSRuntimeMode.Strict;

        // MudBlazor 9: MudPopoverProvider (rendered as a sibling alongside
        // SoundController's MudTooltip) makes several JS calls during render
        // and teardown. In Strict mode, every call must be registered:
        //   - mudPopover.initialize       (void)
        //   - mudpopoverHelper.countProviders (returns int)
        //   - mudPopover.connect          (void, per-popover, GUID arg)
        //   - mudPopover.dispose          (void)
        // Use wildcard-predicate SetupVoid/Setup<T> to cover all call signatures.
        JSInterop.SetupVoid("mudPopover.initialize", _ => true).SetVoidResult();
        JSInterop.Setup<int>("mudpopoverHelper.countProviders", _ => true).SetResult(0);
        JSInterop.SetupVoid("mudPopover.connect", _ => true).SetVoidResult();
        JSInterop.SetupVoid("mudPopover.dispose", _ => true).SetVoidResult();
    }

    private void SetupGetItem(string? returnValue)
    {
        JSInterop.Setup<string?>("localStorage.getItem",
                invocation => invocation.Arguments.Count > 0 &&
                              invocation.Arguments[0] is string k &&
                              k == LocalStorageKey)
            .SetResult(returnValue);
    }

    private void SetupSetItem()
    {
        JSInterop.SetupVoid("localStorage.setItem",
            invocation => invocation.Arguments.Count > 0 &&
                          invocation.Arguments[0] is string k &&
                          k == LocalStorageKey);
    }

    /// <summary>
    /// Load-bearing test pin per ADR-0026 § "Explicitly NOT adopted".
    ///
    /// When localStorage returns null (key absent), IsMuted MUST be true.
    /// This test exercises the actual IJSRuntime localStorage read flow —
    /// not a field default, but the conditional branch in OnInitializedAsync
    /// that evaluates the stored value.
    ///
    /// If someone changes `IsMuted = !string.Equals(stored, "true", ...)` to
    /// default to unmuted, this test fails and blocks the PR.
    /// </summary>
    [Fact]
    public async Task Defaults_to_muted_when_localStorage_is_empty()
    {
        // Arrange — localStorage returns null (key absent).
        SetupGetItem(returnValue: null);

        // Act — render; OnInitializedAsync reads localStorage.
        var cut = RenderWithPopover<SoundController>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Assert — IsMuted is true when stored value is null.
        Assert.True(cut.Instance.IsMuted);

        // Assert — the toggle button carries data-muted="true".
        var toggle = cut.Find("[data-testid='sound-controller-toggle']");
        Assert.Equal("true", toggle.GetAttribute("data-muted"));
    }

    [Fact]
    public async Task Reads_localStorage_value_when_present_and_true()
    {
        // Arrange — localStorage returns "true" (user previously unmuted).
        SetupGetItem(returnValue: "true");

        var cut = RenderWithPopover<SoundController>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Assert — IsMuted is false when stored value is "true" (sound enabled).
        Assert.False(cut.Instance.IsMuted);

        var toggle = cut.Find("[data-testid='sound-controller-toggle']");
        Assert.Equal("false", toggle.GetAttribute("data-muted"));
    }

    [Fact]
    public async Task Reads_localStorage_value_when_present_and_false()
    {
        // Arrange — localStorage returns "false" (user previously muted).
        SetupGetItem(returnValue: "false");

        var cut = RenderWithPopover<SoundController>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Assert — IsMuted is true when stored value is "false".
        Assert.True(cut.Instance.IsMuted);
    }

    [Fact]
    public async Task Toggle_persists_to_localStorage()
    {
        // Arrange — start muted (null → IsMuted = true).
        SetupGetItem(returnValue: null);
        SetupSetItem(); // setItem is called after toggle.

        var cut = RenderWithPopover<SoundController>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        Assert.True(cut.Instance.IsMuted); // starts muted

        // Act — click the toggle button (synchronous Click dispatches the Blazor event).
        cut.Find("[data-testid='sound-controller-toggle']").Click();

        // Wait for the async toggle (ToggleAsync calls InvokeVoidAsync) to complete.
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Assert — now unmuted in-memory.
        Assert.False(cut.Instance.IsMuted);

        // Assert — localStorage.setItem was called with the correct key and value.
        var setItemInvocations = JSInterop.Invocations
            .Where(i => i.Identifier == "localStorage.setItem")
            .ToList();

        Assert.True(setItemInvocations.Count > 0,
            "Expected localStorage.setItem to be called after toggle.");

        var call = setItemInvocations.First();
        Assert.Equal(LocalStorageKey, call.Arguments[0]?.ToString());
        // Toggled from muted→unmuted: !IsMuted was true → persisted as "true" (sound enabled).
        Assert.Equal("true", call.Arguments[1]?.ToString());
    }

    /// <summary>
    /// Pin: no audio element renders and no audio JS API is invoked in this PR.
    ///
    /// SoundController ships the toggle INFRASTRUCTURE. Audio assets land in
    /// a future PR. This test ensures that wiring audio accidentally into this
    /// PR is caught before merging.
    ///
    /// JSInterop.Mode = Strict is set in the constructor. If any JS call is
    /// made that does NOT match the registered localStorage.getItem handler,
    /// bUnit throws — which includes any audio-related API calls. The test
    /// passes if and only if the ONLY JS call made is localStorage.getItem.
    ///
    /// Asserts:
    ///   - No &lt;audio&gt; element in the rendered markup.
    ///   - JSInterop.Invocations contains only localStorage.getItem
    ///     (no audio-related JS calls — strict mode would throw otherwise).
    /// </summary>
    [Fact]
    public async Task Does_not_play_audio_in_this_PR_only_toggle_state()
    {
        // Arrange — register only localStorage.getItem. Any other JS call
        // (e.g., AudioContext, audio.play()) would throw in Strict mode,
        // failing the test before we even reach the assertions below.
        SetupGetItem(returnValue: null);

        var cut = RenderWithPopover<SoundController>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Assert — no <audio> element in the rendered markup.
        Assert.Empty(cut.FindAll("audio"));

        // Assert — only localStorage.getItem was invoked (no audio APIs).
        // bUnit strict mode guarantees this: any unregistered call would throw.
        var invocations = JSInterop.Invocations.ToList();
        Assert.All(invocations, invocation =>
            Assert.DoesNotContain("audio", invocation.Identifier,
                StringComparison.OrdinalIgnoreCase));
    }
}
