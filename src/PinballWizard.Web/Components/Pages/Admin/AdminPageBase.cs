using Microsoft.AspNetCore.Components;

namespace PinballWizard.Web.Components.Pages.Admin;

// Base for interactive admin pages that load data asynchronously (from
// OnAfterRenderAsync or event handlers). Guards against the Blazor circuit
// being disposed (user navigates away) mid-load:
//  - SafeStateHasChanged() no-ops after disposal, so a late async continuation
//    can't call StateHasChanged() on a disposed component (ObjectDisposedException, #615);
//  - CreateLoadCts() links a per-load timeout to component lifetime so the
//    in-flight query is cancelled when the component is disposed.
public abstract class AdminPageBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _disposalCts = new();
    private bool _disposed;

    // Cancels when the component is disposed. Pass to async work that has no
    // timeout of its own so it stops on navigation-away.
    protected CancellationToken ComponentDisposalToken => _disposalCts.Token;

    // Re-render only if the component is still alive.
    protected void SafeStateHasChanged()
    {
        if (_disposed)
        {
            return;
        }

        StateHasChanged();
    }

    // A timeout CTS linked to component lifetime: cancels on timeout OR dispose.
    protected CancellationTokenSource CreateLoadCts(TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);
        cts.CancelAfter(timeout);
        return cts;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposalCts.Cancel();
        _disposalCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
