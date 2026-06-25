namespace PinballWizard.Application.Jobs;

// Typed exception surfaced when the ARM jobs service cannot reach Azure
// (auth failure, network error, resource not found, 429, etc.).
//
// The Blazor page catches this and renders a visible ARM-error state
// (MudAlert Severity.Error + data-testid="jobs-arm-error") per Invariant #17:
// degrade visibly, never present synthetic/placeholder data as real output.
public sealed class ArmJobAdminException : Exception
{
    public ArmJobAdminException(string message, Exception? inner = null)
        : base(message, inner) { }
}
