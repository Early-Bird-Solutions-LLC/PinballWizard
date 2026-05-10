using PinballWizard.Application.Ai;

namespace PinballWizard.Web.Components.Degraded;

// Scoped-per-circuit degradation state store.
//
// Components that receive a WizardAnswer with a non-None DegradationContext
// call SetDegradation(...) to notify the rest of the layout.
// OutageBanner subscribes to OnChanged in OnInitialized and
// unsubscribes in Dispose — a simple mutable record + event Action
// pattern is sufficient for showcase-scale state propagation.
//
// This is NOT a full state-management library. It is the minimal
// Blazor-idiomatic pattern: scoped service + event notification.
// A sceptical architect reading this sees "standard INotifyPropertyChanged
// equivalent" rather than "accidental over-engineering."
//
// ADR-0026 § 5 — graceful degradation surface.
// ADR-0026 § 6 — OutageBanner is one of the four locked delight surfaces.
public interface IClientDegradationStore
{
    /// <summary>
    /// Active degradation context, or <c>null</c> when the session is healthy.
    /// </summary>
    DegradationContext? Current { get; }

    /// <summary>
    /// Raised on the Blazor synchronisation context whenever <see cref="Current"/> changes.
    /// OutageBanner subscribes; call <c>StateHasChanged()</c> in the handler.
    /// </summary>
    event Action OnChanged;

    /// <summary>
    /// Update the active degradation state. Raises <see cref="OnChanged"/>.
    /// Pass <c>null</c> to clear (session recovered).
    /// </summary>
    void SetDegradation(DegradationContext? context);

    /// <summary>Dismiss / hide the banner for this session. Does not clear degradation.</summary>
    void Dismiss();

    /// <summary>Whether the user has dismissed the banner this session.</summary>
    bool IsDismissed { get; }
}
