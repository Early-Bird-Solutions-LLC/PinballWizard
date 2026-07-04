using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components;
using PinballWizard.Web.Components.Degraded;
using PinballWizard.Web.Components.Wizard;
using PinballWizard.Web.Security;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// Builds a fresh minimal WebApplication for Playwright accessibility tests.
//
// Why not WebApplicationFactory<App>?
// WebApplicationFactory<TEntryPoint> invokes the real Program.cs, which
// registers AddMicrosoftIdentityWebApp (OpenIdConnect). ConfigureTestServices
// runs BEFORE Program.cs in the WebApplicationFactory lifecycle, so OIDC
// Configure actions registered by Program.cs override the test overrides.
// The OIDC middleware then challenges Blazor's /_blazor circuit upgrade
// (no auth cookie in the headless browser) and returns its XHTML form-post
// page (lang="iv" / InvariantCulture). Playwright follows the form's
// JavaScript auto-submit, and axe-core scans the OIDC page rather than
// the actual Blazor app.
//
// This factory builds only what is needed for the UI to render:
//   Blazor (razor + server + WASM) / MudBlazor / IClientDegradationStore
//   No-op auth (TestAuthHandler) / stub HTTP clients / no OIDC / no AI
//
// The app URL is read from IServerAddressesFeature after startup.
public class PlaywrightWebApplicationFactory : IAsyncLifetime
{
    private readonly bool _adminMode;
    private WebApplication? _app;

    // Public parameterless ctor — the only public constructor, required by xUnit's
    // fixture activator. Used directly by AccessibilityTests (public anonymous mode).
    public PlaywrightWebApplicationFactory() => _adminMode = false;

    // Protected ctor used by derived types (e.g. AdminPlaywrightFactory) that need
    // admin mode. Not public: xUnit fixture activator sees only one public ctor.
    protected PlaywrightWebApplicationFactory(bool adminMode) => _adminMode = adminMode;

    public string ServerAddress
    {
        get
        {
            if (_app is null)
                throw new InvalidOperationException("InitializeAsync not called.");

            var server = _app.Services.GetRequiredService<IServer>();
            return server.Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();
        }
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // Blazor auto-render mode (Server + WASM) — identical to Program.cs.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();

        // MudBlazor chrome (ADR-0008).
        builder.Services.AddMudServices();

        // Scoped services depended on by Blazor components.
        builder.Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();
        builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();

        // Stub HTTP clients: base addresses point nowhere but the Index page
        // has a compiled-in fallback for when the landing endpoint is down.
        builder.Services
            .AddHttpClient<IWizardLandingClient, WizardLandingClient>(
                c => c.BaseAddress = new Uri("http://127.0.0.1:1"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler());

        builder.Services
            .AddHttpClient<IWizardStreamingClient, WizardStreamingClient>(
                c => c.BaseAddress = new Uri("http://127.0.0.1:1"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler());

        // LandingHero (ADR-0049 phase 3) injects IMachineSuggestClient for the
        // typeahead. It MUST be registered here or the Index page's hero cannot be
        // constructed and the whole page renders empty (same failure class as the
        // /settings IUserPreferencesService exclusion). The 503 stub makes the
        // client return [] → the autocomplete simply shows no suggestions.
        builder.Services
            .AddHttpClient<IMachineSuggestClient, MachineSuggestClient>(
                c => c.BaseAddress = new Uri("http://127.0.0.1:1"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler());

        // No-op auth — no OIDC, no challenge, no redirect.
        builder.Services
            .AddAuthentication(defaultScheme: "Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

        // Admin mode reproduces Program.cs's no-tenant posture: the permissive
        // AdminOnly policy (RequireAssertion(_ => true)) so /admin/* pages render
        // for the anonymous TestAuthHandler identity. Public mode keeps the bare
        // AddAuthorization() (no AdminOnly policy) — unchanged for the public axe suite.
        if (_adminMode)
        {
            builder.Services.AddAuthorization(o =>
                o.AddPolicy(AuthorizationPolicies.AdminOnly, p => p.RequireAssertion(_ => true)));
            // Required so AdminLayout's <AuthorizeView Policy="AdminOnly"> has a
            // cascading Task<AuthenticationState> and does not throw during render
            // (which produces an empty document that axe reports as failing).
            // AddRazorComponents().AddInteractiveServerComponents() (called above)
            // registers ServerAuthenticationStateProvider as the backing store.
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddAdminTestDoubles();
        }
        else
        {
            builder.Services.AddAuthorization();
        }

        // Random loopback port; actual address is read from IServerAddressesFeature.
        builder.WebHost.ConfigureKestrel(opts => opts.Listen(System.Net.IPAddress.Loopback, 0));

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseAntiforgery();
        app.UseAuthentication();
        app.UseAuthorization();

        // MapStaticAssets() requires a staticwebassets.endpoints.json manifest
        // that only exists in the published Web project, not in the test host.
        // For SSR accessibility testing we don't need fingerprinted static assets —
        // axe runs on the server-rendered HTML (DOMContentLoaded) before Blazor.js
        // initialises, which is the content screen readers and crawlers encounter.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddInteractiveWebAssemblyRenderMode()
            .AddAdditionalAssemblies(typeof(PinballWizard.Web.Client._Imports).Assembly);

        _app = app;
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    // Returns 503 for all requests; used by stub HTTP clients so the
    // IWizardLandingClient and IWizardStreamingClient gracefully fall back.
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
