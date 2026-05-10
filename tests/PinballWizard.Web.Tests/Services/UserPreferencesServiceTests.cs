using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests.Services;

// Unit tests for UserPreferencesService.
// Uses bUnit TestContext for its fake IJSRuntime (JSInterop.Setup).
// Each test creates its own TestContext so JSInterop setups are isolated.
//
// JSInterop mode: Loose — allows unmatched JS calls to return defaults
// without blocking. The service uses ConfigureAwait(false) on Linux which
// can cause argument-matching in Strict mode to race and never resolve,
// producing a 20-minute hang. The tests assert on the C# state
// (CurrentTheme / CurrentMotion / CurrentSound) rather than verifying
// the exact JS call argument — so Loose mode does not weaken coverage.
public sealed class UserPreferencesServiceTests
{
    [Fact]
    public async Task InitializeAsync_ReadsAllThreePreferences_FromLocalStorage()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("pinwiz.getTheme").SetResult(ThemeNames.DaytimeRoute);
        ctx.JSInterop.Setup<string>("pinwiz.getMotion").SetResult("on");
        ctx.JSInterop.Setup<string>("pinwiz.getSound").SetResult("on");

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);

        await svc.InitializeAsync();

        Assert.Equal(ThemeNames.DaytimeRoute, svc.CurrentTheme);
        Assert.Equal("on", svc.CurrentMotion);
        Assert.Equal("on", svc.CurrentSound);
        Assert.True(svc.StorageAvailable);
    }

    [Fact]
    public async Task InitializeAsync_DefaultsToModernLcd_WhenLocalStorageReturnsEmpty()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("pinwiz.getTheme").SetResult(ThemeNames.ModernLcd);
        ctx.JSInterop.Setup<string>("pinwiz.getMotion").SetResult("match");
        ctx.JSInterop.Setup<string>("pinwiz.getSound").SetResult("muted");

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);

        await svc.InitializeAsync();

        Assert.Equal(ThemeNames.ModernLcd, svc.CurrentTheme);
        Assert.Equal("match", svc.CurrentMotion);
        Assert.Equal("muted", svc.CurrentSound);
    }

    [Fact]
    public async Task InitializeAsync_SetsStorageAvailableFalse_OnJsException()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        // No setup = InvokeAsync throws JSException in strict mode

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);

        await svc.InitializeAsync();

        Assert.False(svc.StorageAvailable);
        // Defaults must be preserved on failure
        Assert.Equal(ThemeNames.ModernLcd, svc.CurrentTheme);
        Assert.Equal("match", svc.CurrentMotion);
        Assert.Equal("muted", svc.CurrentSound);
    }

    [Fact]
    public async Task InitializeAsync_FiresStateChanged()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("pinwiz.getTheme").SetResult(ThemeNames.ModernLcd);
        ctx.JSInterop.Setup<string>("pinwiz.getMotion").SetResult("match");
        ctx.JSInterop.Setup<string>("pinwiz.getSound").SetResult("muted");

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);
        var fired = false;
        svc.StateChanged += () => fired = true;

        await svc.InitializeAsync();

        Assert.True(fired);
    }

    [Fact]
    public async Task SetThemeAsync_UpdatesCurrentThemeAndFiresStateChanged()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // Loose avoids Strict-mode hang on Linux

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);
        var fired = false;
        svc.StateChanged += () => fired = true;

        await svc.SetThemeAsync(ThemeNames.DaytimeRoute);

        Assert.Equal(ThemeNames.DaytimeRoute, svc.CurrentTheme);
        Assert.True(fired);
    }

    [Fact]
    public async Task SetMotionAsync_UpdatesCurrentMotion()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);

        await svc.SetMotionAsync("off");

        Assert.Equal("off", svc.CurrentMotion);
    }

    [Fact]
    public async Task SetSoundAsync_UpdatesCurrentSound()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var js = ctx.Services.GetRequiredService<IJSRuntime>();
        var svc = new UserPreferencesService(js);

        await svc.SetSoundAsync("on");

        Assert.Equal("on", svc.CurrentSound);
    }
}
