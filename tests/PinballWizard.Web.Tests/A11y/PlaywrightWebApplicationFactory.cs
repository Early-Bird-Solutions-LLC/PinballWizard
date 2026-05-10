using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Web.Components;

namespace PinballWizard.Web.Tests.A11y;

// Starts a real Kestrel listener on a random loopback port alongside the
// TestServer-based host that WebApplicationFactory<T> uses internally.
// Playwright (a real browser process) must connect over a TCP socket —
// TestServer's in-process pipe is not usable from an external browser.
//
// Type parameter: App (the Blazor root component in PinballWizard.Web).
// Using App rather than Program avoids the ambiguity between PinballWizard.Api
// and PinballWizard.Web, both of which have a top-level-statement Program class.
// WebApplicationFactory<T> uses typeof(T).Assembly, so any public type from
// the target project works.
//
// Pattern: build the IHostBuilder twice — once for TestServer (returned to
// base class) and once for Kestrel (held internally, surfaced via ServerAddress).
public sealed class PlaywrightWebApplicationFactory : WebApplicationFactory<App>
{
    private IHost? _kestrelHost;

    // The base address that Playwright pages should navigate to.
    public string ServerAddress
    {
        get
        {
            EnsureServer();
            return _kestrelHost!.Services
                .GetRequiredService<IServer>()
                .Features.GetRequiredFeature<IServerAddressesFeature>()
                .Addresses
                .First();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development environment skips HSTS + HTTPS redirection — both
        // require a real certificate and TLS terminator that the test host
        // does not provide. Matches the condition in Program.cs (line 127).
        builder.UseEnvironment("Development");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Build 1: TestServer-based host — returned to base class so the
        // base WebApplicationFactory API (CreateClient, etc.) continues to
        // work for any other tests sharing this fixture type.
        var testHost = builder.Build();

        // Build 2: real Kestrel host on a random loopback port.
        // Playwright needs a real TCP socket; TestServer is in-process only.
        builder.ConfigureWebHost(b =>
            b.UseKestrel(opts => opts.Listen(IPAddress.Loopback, port: 0)));

        _kestrelHost = builder.Build();
        _kestrelHost.Start();

        return testHost;
    }

    // Trigger host initialization before ServerAddress is read, in case no
    // CreateClient() call has been made yet by the fixture lifecycle.
    private void EnsureServer()
    {
        if (_kestrelHost is null)
            _ = CreateClient();
    }

    protected override void Dispose(bool disposing)
    {
        _kestrelHost?.StopAsync().GetAwaiter().GetResult();
        _kestrelHost?.Dispose();
        base.Dispose(disposing);
    }
}
