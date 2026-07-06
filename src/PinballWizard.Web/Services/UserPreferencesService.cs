using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace PinballWizard.Web.Services;

// Locally-persisted user preferences: theme, motion, sound.
// Reads/writes localStorage via JS interop (app.js pinwiz.*).
// Scoped per Blazor circuit; call InitializeAsync in OnAfterRenderAsync
// so JS is available before reading localStorage.
//
// ADR-0026 § 6  — sound muted by default
// ADR-0027 § 10 — no per-user analytics, no engagement-metric surfaces
// docs/ui/screens/settings.md — three preferences, localStorage keys

public static class ThemeNames
{
    public const string Backbox = "backbox";
    public const string Cabinet = "cabinet";
    public const string DaytimeRoute = "daytime-route";
    public const string DmdClassic = "dmd-classic";
    public const string ModernLcd = "modern-lcd";
    public const string Paper = "paper";
}

public static class PreferenceKeys
{
    public const string Theme = "pinwiz.theme";
    public const string Motion = "pinwiz.motion";
    public const string Sound = "pinwiz.sound";
    public const string PageSize = "pinwiz.pageSize";
}

public interface IUserPreferencesService
{
    string CurrentTheme { get; }   // ThemeNames.*
    string CurrentMotion { get; }  // "match" | "on" | "off"
    string CurrentSound { get; }   // "muted" | "on"
    int PageSize { get; }
    bool StorageAvailable { get; }
    event Action? StateChanged;
    Task InitializeAsync();
    Task SetThemeAsync(string theme);
    Task SetMotionAsync(string motion);
    Task SetSoundAsync(string sound);
    Task SetPageSizeAsync(int pageSize);
}

public sealed class UserPreferencesService(IJSRuntime js, ILogger<UserPreferencesService>? logger = null) : IUserPreferencesService
{
    public string CurrentTheme { get; private set; } = ThemeNames.Paper;
    public string CurrentMotion { get; private set; } = "match";
    public string CurrentSound { get; private set; } = "muted";
    public int PageSize { get; private set; } = 10;
    public bool StorageAvailable { get; private set; } = true;

    public event Action? StateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            CurrentTheme = await js.InvokeAsync<string>("pinwiz.getTheme").ConfigureAwait(false);
            CurrentMotion = await js.InvokeAsync<string>("pinwiz.getMotion").ConfigureAwait(false);
            CurrentSound = await js.InvokeAsync<string>("pinwiz.getSound").ConfigureAwait(false);
            var pageSizeStr = await js.InvokeAsync<string>("pinwiz.getPageSize").ConfigureAwait(false);
            if (int.TryParse(pageSizeStr, out var ps))
            {
                PageSize = ps;
            }
        }
        catch (JSException)
        {
            StorageAvailable = false;
        }
        StateChanged?.Invoke();
    }

    public async Task SetThemeAsync(string theme)
    {
        try { await js.InvokeVoidAsync("pinwiz.setTheme", theme).ConfigureAwait(false); }
        catch (JSException ex) { logger?.LogDebug(ex, "localStorage write failed for {Preference}; preference stored in-memory only.", "theme"); }
        CurrentTheme = theme;
        StateChanged?.Invoke();
    }

    public async Task SetMotionAsync(string motion)
    {
        try { await js.InvokeVoidAsync("pinwiz.setMotion", motion).ConfigureAwait(false); }
        catch (JSException ex) { logger?.LogDebug(ex, "localStorage write failed for {Preference}; preference stored in-memory only.", "motion"); }
        CurrentMotion = motion;
        StateChanged?.Invoke();
    }

    public async Task SetSoundAsync(string sound)
    {
        try { await js.InvokeVoidAsync("pinwiz.setSound", sound).ConfigureAwait(false); }
        catch (JSException ex) { logger?.LogDebug(ex, "localStorage write failed for {Preference}; preference stored in-memory only.", "sound"); }
        CurrentSound = sound;
        StateChanged?.Invoke();
    }

    public async Task SetPageSizeAsync(int pageSize)
    {
        try { await js.InvokeVoidAsync("pinwiz.setPageSize", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false); }
        catch (JSException ex) { logger?.LogDebug(ex, "localStorage write failed for {Preference}; preference stored in-memory only.", "pageSize"); }
        PageSize = pageSize;
        StateChanged?.Invoke();
    }
}
