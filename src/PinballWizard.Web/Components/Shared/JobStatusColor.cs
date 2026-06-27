using MudBlazor;

namespace PinballWizard.Web.Components.Shared;

internal static class JobStatusColor
{
    internal static Color For(string status) => status switch
    {
        "Succeeded" => Color.Success,
        "Running" or "Processing" => Color.Info,
        "Failed" => Color.Error,
        "Stopped" or "Degraded" => Color.Warning,
        _ => Color.Default,
    };
}
