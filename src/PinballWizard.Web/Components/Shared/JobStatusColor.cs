using MudBlazor;

namespace PinballWizard.Web.Components.Shared;

internal static class JobStatusColor
{
    internal static Color For(string status) => status switch
    {
        "Succeeded" or "Running" or "Processing" => Color.Success, // active/healthy
        "Failed" or "Degraded" or "Stopped" => Color.Error,        // problem/terminal
        _ => Color.Default,
    };
}
