using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace PinballWizard.Web.Tests;

// Base class for bUnit tests that inherit from BunitContext.
//
// BunitContext.Dispose() calls Services.Dispose() which throws for services
// that implement only IAsyncDisposable (e.g. MudBlazor.KeyInterceptorService,
// MudBlazor.PopoverService registered by AddMudServices()). xunit v2 calls
// the synchronous Dispose() on the test class, which triggers the exception.
//
// Fix: implement IAsyncLifetime so xunit uses DisposeAsync() for teardown.
// BunitContext.DisposeAsync() handles async-only disposable services correctly.
// BunitContext.Dispose() checks _disposed and is a no-op after async disposal.
public abstract class AsyncBunitContext : BunitContext, IAsyncLifetime
{
    // Components consulting RendererInfo.IsInteractive (e.g.
    // WizardAnswerStream gates its auto-submit so it can't run during SSR
    // prerender) need RendererInfo set or bUnit throws
    // MissingRendererInfoException. Call this at the END of the derived
    // test-class constructor — accessing Renderer materializes the service
    // provider, so it must come after all service registrations.
    // Interactive-server matches the mode every interactive surface uses
    // in production (ADR-0026 follow-up (2)).
    protected void UseInteractiveServerRendererInfo() =>
        Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

    // MudBlazor 9 requires <MudPopoverProvider /> to be present in the same
    // render tree as any popover-capable component (MudSelect, MudMenu,
    // MudTooltip, MudAutocomplete, MudDataGrid, MudTabs, MudList, etc.).
    // In v8 these components could be rendered without the provider; v9 throws
    // "Missing <MudPopoverProvider />" at render time.
    //
    // The provider takes no ChildContent, so it renders as a SIBLING fragment
    // alongside the component under test rather than as a render-tree wrapper.
    // These helpers encapsulate that pattern and return the typed component
    // handle so call sites are identical to Render<T>() / Render<T>(builder).
    protected IRenderedComponent<TComponent> RenderWithPopover<TComponent>()
        where TComponent : IComponent
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<TComponent>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<TComponent>();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync().ConfigureAwait(false);
}
