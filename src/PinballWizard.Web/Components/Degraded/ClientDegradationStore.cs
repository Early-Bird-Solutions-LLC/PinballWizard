using PinballWizard.Application.Ai;

namespace PinballWizard.Web.Components.Degraded;

// Scoped-per-circuit implementation.
//
// Scoped lifetime means one instance per Blazor circuit (one per user session
// on the Server render mode). Components on the same circuit share the same
// instance, so OutageBanner and WizardShell can coordinate without a static
// or singleton.
//
// Thread-safety: Blazor Server runs component callbacks on the circuit's
// sync context (single-threaded per circuit). The event Action pattern
// is therefore safe without locks.
//
// ADR-0026 § 5, § 6.
// Internal sealed — accessible to test assembly via InternalsVisibleTo
// in PinballWizard.Web.csproj (added by PR-D-degraded).
// Tests register via Services.AddScoped<IClientDegradationStore, ClientDegradationStore>().
internal sealed class ClientDegradationStore : IClientDegradationStore
{
    public DegradationContext? Current { get; private set; }

    public bool IsDismissed { get; private set; }

    public event Action OnChanged = static () => { };

    public void SetDegradation(DegradationContext? context)
    {
        Current = context;
        IsDismissed = false;    // new degradation resets any prior dismiss.
        OnChanged();
    }

    public void Dismiss()
    {
        IsDismissed = true;
        OnChanged();
    }
}
