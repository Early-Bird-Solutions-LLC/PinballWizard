using Microsoft.JSInterop;

namespace PinballWizard.Web.Components.Layout;

// Thin wrapper over the window.pinwiz.navRail JS helpers. Nav preference is
// auxiliary — JS-interop failures (prerender, private-mode storage) degrade to a
// no-op rather than surfacing, but never fabricate a "true" preference.
internal static class NavRailStorage
{
    public static async Task<bool?> GetPinnedAsync(IJSRuntime js, string key)
    {
        try
        {
            return await js.InvokeAsync<bool?>("pinwiz.navRail.get", key);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task SetPinnedAsync(IJSRuntime js, string key, bool pinned)
    {
        try
        {
            await js.InvokeVoidAsync("pinwiz.navRail.set", key, pinned);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Auxiliary preference — safe to drop.
        }
    }
}
