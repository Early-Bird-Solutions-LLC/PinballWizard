using Bunit;
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
        Renderer.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync().ConfigureAwait(false);
}
