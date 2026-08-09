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
using PinballWizard.Web.Engineering;
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

        // Engineering live-docs provider — the /engineering pages inject it.
        // Mirrors Program.cs; reads embedded docs from the Web assembly, no
        // external deps, so it works unchanged in this minimal host. Without
        // it the /engineering routes 500 during SSR (axe then sees an empty
        // document) — guarded by EngineeringSsrSmokeTests.
        builder.Services.AddSingleton<IEngineeringDocsProvider, EngineeringDocsProvider>();

        // AdminStatusFooter (in the admin nav-rail footer, so on every /admin/* page)
        // @injects BuildInfo. It MUST be registered here or the admin chrome cannot be
        // constructed and every admin page renders empty during SSR — the same
        // "renders empty" failure class as the IEngineeringDocsProvider /
        // IMachineSuggestClient / IGridSearchClient registrations above. Program.cs
        // registers it for the real host; this minimal test host must mirror it.
        // No env vars here → BuildInfo yields its honest local fallback (Invariant #17).
        builder.Services.AddSingleton<BuildInfo>();

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

        // Every admin data grid now renders GridSearch (AppDataGrid's EnableAiSearch
        // defaults true), which @injects IGridSearchClient — same "renders empty"
        // failure class as the two exclusions noted above if left unregistered.
        // SearchAsync only fires on an explicit user query, never during initial
        // render, so the 503 stub is safe here too.
        builder.Services
            .AddHttpClient<IGridSearchClient, GridSearchClient>(
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

        // Serve the real stylesheets (#790). WITHOUT this the host has no web root
        // (there is no wwwroot under the test project's bin), so every request for
        // app.css / PinballWizard.Web.styles.css / _content/MudBlazor/MudBlazor.min.css
        // fell through to the Blazor catch-all route and was answered with the HTML
        // error page — at 200 OK, content-type text/html. The browser parsed ~48KB of
        // HTML as CSS, yielding 8 CSS rules in total, and axe scanned a completely
        // unstyled DOM. Any CSS-dependent rule (target-size, and the planned overflow
        // invariants) therefore could not fail for the right reason, and a status-code
        // check would NOT have caught it — only a computed-style probe does.
        //
        // Two things had to be true for the assets to load, and neither was:
        //
        // 1. WebApplication.CreateBuilder calls UseStaticWebAssets automatically ONLY in
        //    the Development environment. This host sets no env vars, so it resolves to
        //    Production and the call never happened.
        // 2. Calling UseStaticWebAssets() alone is still not enough here. Per
        //    StaticWebAssetsLoader.ResolveManifest, with no explicit configuration key it
        //    looks for "{environment.ApplicationName}.staticwebassets.runtime.json" — and
        //    under `dotnet test` the entry assembly is the VSTest *testhost*, so
        //    ApplicationName is "testhost" and it hunts for a manifest that does not
        //    exist. On a miss it returns null SILENTLY ("a missing manifest might simply
        //    mean the feature is not enabled"), so the failure is invisible.
        //
        // Hence the explicit manifest path below, plus a hard assert: a silent no-op is
        // precisely the failure mode this issue exists to eliminate, so if the manifest
        // ever stops being copied the suite must break loudly rather than quietly resume
        // scanning unstyled pages.
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey, ResolveManifest("runtime"));
        builder.WebHost.UseStaticWebAssets();

        var app = builder.Build();

        app.UseAntiforgery();
        app.UseAuthentication();
        app.UseAuthorization();

        // Note: this previously read "MapStaticAssets() requires a
        // staticwebassets.endpoints.json manifest that only exists in the published
        // Web project, not in the test host." That was wrong — the test bin carries
        // its own PinballWizard.Web.Tests.staticwebassets.endpoints.json — and the
        // belief that static assets were simply unavailable here is what let the
        // unstyled-DOM false-green survive (#790).
        //
        // MapStaticAssets serves the stylesheets, mirroring Program.cs:409. A
        // UseStaticFiles() call used to sit above and has been removed: it was dead
        // middleware, and that is the second half of the #790 harness bug.
        // StaticFileMiddleware declines whenever routing has already selected an
        // endpoint
        // (StaticFileMiddleware.ValidateNoEndpointDelegate — "context.GetEndpoint()
        // ?.RequestDelegate is null"), and MapRazorComponents' catch-all "/{**slug}"
        // route matches literally every path, /app.css included. So the request skipped
        // the file middleware, fell through to the catch-all, and was answered with the
        // rendered HTML error page at 200 OK. There is no path on which UseStaticFiles
        // could ever have fired here. MapStaticAssets registers real endpoints, which
        // outrank the catch-all and win the route match.
        //
        // Axe still runs on the server-rendered HTML at DOMContentLoaded, before
        // Blazor.js initialises — that is the content screen readers and crawlers
        // encounter — but it now runs against it *styled*.
        app.MapStaticAssets(ResolveManifest("endpoints")).AllowAnonymous();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddInteractiveWebAssemblyRenderMode()
            .AddAdditionalAssemblies(typeof(PinballWizard.Web.Client._Imports).Assembly);

        _app = app;
        await _app.StartAsync();
    }

    // Locates a static-web-assets manifest in the test output ("runtime" or "endpoints").
    //
    // Passed explicitly because the framework's own lookup cannot find it here: both
    // StaticWebAssetsLoader and MapStaticAssets default to
    // "{ApplicationName}.staticwebassets.<kind>.json", and under `dotnet test` the entry
    // assembly is the VSTest testhost — so ApplicationName is "testhost" and the lookup
    // misses. StaticWebAssetsLoader treats a miss as "feature not enabled" and returns
    // null SILENTLY, which is how the unstyled-DOM false-green stayed invisible (#790).
    //
    // The throw is therefore the point: if the manifest ever stops being copied to the
    // output, this suite must fail loudly rather than quietly go back to scanning
    // unstyled pages and reporting green.
    private static string ResolveManifest(string kind)
    {
        // Path.GetFileName strips any directory or root component before combining.
        // Neither input can actually be rooted — an assembly simple name cannot contain
        // a path separator, and `kind` is a literal at both call sites — so this is
        // defence in depth rather than a live bug, but it costs nothing and keeps
        // Path.Combine from ever silently discarding AppContext.BaseDirectory.
        var name = typeof(PlaywrightWebApplicationFactory).Assembly.GetName().Name;
        var fileName = Path.GetFileName($"{name}.staticwebassets.{kind}.json");
        var path = Path.Combine(AppContext.BaseDirectory, fileName);

        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Static web assets '{kind}' manifest not found at '{path}'. Without it this host "
                + "serves no stylesheets, and every CSS-dependent accessibility assertion silently "
                + "passes against an unstyled DOM (#790).");

        return path;
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
