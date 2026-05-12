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
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync().ConfigureAwait(false);
}
