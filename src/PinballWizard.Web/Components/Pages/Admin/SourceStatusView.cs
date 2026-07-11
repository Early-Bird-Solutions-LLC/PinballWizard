using MudBlazor;

namespace PinballWizard.Web.Components.Pages.Admin;

// Four-state ingestion-source status shown on /admin/sources. Distinguishes a
// deliberate "no such content exists" (NoSource) and "blocked, exists elsewhere"
// (Deferred) from a plain manual off-switch (Disabled) — so a disabled row reads
// as a documented decision, not a failure.
public enum SourceStatus { Active, NoSource, Deferred, Disabled }

// Presentation projection of a status: label + colour + icon. Icon is always set
// so colour is never the sole carrier of meaning (WCAG 2.1 AA).
public sealed record SourceStatusView(SourceStatus Status, string Label, Color Color, string Icon)
{
    public static SourceStatusView Derive(bool enabled, string? discoveryStatus)
    {
        // Enabled sources are Active regardless of any recorded discovery note.
        if (enabled)
        {
            return new SourceStatusView(
                SourceStatus.Active, "Active", Color.Success, Icons.Material.Filled.CheckCircle);
        }

        return discoveryStatus switch
        {
            // NoSource and Disabled share Color.Default — both are neutral, not error states.
            "NoSource" => new SourceStatusView(
                SourceStatus.NoSource, "No source", Color.Default, Icons.Material.Filled.RemoveCircleOutline),
            "Deferred" => new SourceStatusView(
                SourceStatus.Deferred, "Deferred", Color.Default, Icons.Material.Filled.PauseCircleOutline),
            _ => new SourceStatusView(
                SourceStatus.Disabled, "Disabled", Color.Default, Icons.Material.Filled.Block),
        };
    }
}
