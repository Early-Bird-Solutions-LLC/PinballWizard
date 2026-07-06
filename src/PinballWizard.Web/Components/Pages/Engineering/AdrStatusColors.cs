using MudBlazor;

namespace PinballWizard.Web.Components.Pages.Engineering;

internal static class AdrStatusColors
{
    internal static Color ForStatus(string status) =>
        status.Trim().ToLowerInvariant() switch
        {
            "accepted"   => Color.Success,
            "deprecated" => Color.Warning,
            "superseded" => Color.Warning,
            "proposed"   => Color.Info,
            _            => Color.Default,
        };
}
