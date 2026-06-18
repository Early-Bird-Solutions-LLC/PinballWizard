using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components;
using PinballWizard.Web.Components.Degraded;
using PinballWizard.Web.Components.Wizard;
using PinballWizard.Web.Services;
using PinballWizard.Web.Tests.A11y;
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// Runs the REAL PinballWizard.Web app pipeline (real MapStaticAssets → live
// Blazor circuit) with AzureAd:TenantId unset (no OIDC; permissive AdminOnly),
// the admin backends replaced by AddAdminTestDoubles, on a real Kestrel
// loopback port so Playwright can connect a real browser.
//
// Why self-built (not WebApplicationFactory<App>)?
// WAF<App>'s ConfigureTestServices hook runs AFTER Program.cs, so the service-
// override order is fine here (no OIDC branch fires — TenantId is empty).
// However WAF binds an in-memory TestServer that a real browser cannot reach.
// The proven pattern (PlaywrightWebApplicationFactory) builds a WebApplication
// directly on a Kestrel loopback port and reads the address from
// IServerAddressesFeature. This factory does the same but:
//   (a) calls MapStaticAssets() (requires the built Web project manifest), and
//   (b) uses AddAdminTestDoubles to stub every admin-page backend.
//
// Content-root note: MapStaticAssets() discovers the staticwebassets manifest
// from the app's content root. We point the content root at the built Web
// project output (bin/Debug/net10.0 of PinballWizard.Web) where the manifest
// lives after `dotnet build src/PinballWizard.Web`. The test project's own
// output directory only contains the test assembly; it does not carry the
// manifest, so ContentRoot must be overridden.
//
// See spec §5.1 and task-4-brief.md for the full decision ladder.
public sealed class InteractiveAdminWebApplicationFactory : IAsyncLifetime
{
    private WebApplication? _app;

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
        // Resolve the content root: the built PinballWizard.Web output directory,
        // which contains PinballWizard.Web.staticwebassets.endpoints.json that
        // MapStaticAssets() requires. The test assembly lives next to the Web
        // output in the shared test project bin folder after build.
        var (contentRoot, manifestPath) = ResolveWebContentRoot();

        // Use the source project's wwwroot as the web root: this is where the
        // StaticAssetDevelopmentRuntimeHandler expects to find app.css, app.js,
        // etc. The contentRoot (bin output) holds the manifest; the sourceRoot
        // holds the actual files that the manifest references.
        var sourceWebRoot = ResolveSourceWebRoot();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            WebRootPath = sourceWebRoot,
            // ApplicationName must match the manifest file name: UseStaticWebAssets()
            // looks for {ApplicationName}.staticwebassets.runtime.json in the
            // content root. Without this, it defaults to "testhost" and cannot
            // find "PinballWizard.Web.staticwebassets.runtime.json".
            ApplicationName = "PinballWizard.Web",
            // Development: UseStaticWebAssets (called below) wires the runtime
            // manifest so MapStaticAssets can locate physical files for the
            // StaticAssetDevelopmentRuntimeHandler (blazor.web.js, MudBlazor JS/CSS).
            EnvironmentName = "Development",
        });

        // AzureAd:TenantId absent → the no-OIDC, permissive-AdminOnly branch
        // (mirrors Program.cs else-branch). Explicitly clear in case the machine
        // env has it set.
        builder.Configuration["AzureAd:TenantId"] = string.Empty;

        // ── Razor components (Blazor Web App, auto-render mode) ───────────
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();

        // ── MudBlazor ─────────────────────────────────────────────────────
        builder.Services.AddMudServices();

        // ── Auth: no OIDC, permissive AdminOnly ───────────────────────────
        // Mirror the else-branch of Program.cs when TenantId is empty.
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy => policy.RequireAssertion(_ => true));
        });
        builder.Services.AddControllersWithViews();

        // ── Admin-page backends (test doubles, no Cosmos/Foundry) ──────────
        builder.Services.AddAdminTestDoubles();

        // ── Supporting services used by admin pages ────────────────────────
        builder.Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();
        builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();

        // ── Stub HTTP clients (admin pages don't use the wizard SSE path) ─
        builder.Services
            .AddHttpClient<IWizardLandingClient, WizardLandingClient>(
                c => c.BaseAddress = new Uri("http://127.0.0.1:1"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler());
        builder.Services
            .AddHttpClient<IWizardStreamingClient, WizardStreamingClient>(
                c => c.BaseAddress = new Uri("http://127.0.0.1:1"))
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler());

        // UseStaticWebAssets wires the runtime static-assets manifest
        // (PinballWizard.Web.staticwebassets.runtime.json) so the
        // StaticAssetDevelopmentRuntimeHandler can resolve asset files from
        // the source project's wwwroot and NuGet package wwwroot directories.
        // Without this, MapStaticAssets in a Build-manifest context cannot
        // find the physical files for blazor.web.js, MudBlazor CSS/JS, etc.
        builder.WebHost.UseStaticWebAssets();

        // ── Kestrel: loopback, random port ────────────────────────────────
        builder.WebHost.ConfigureKestrel(opts =>
            opts.Listen(System.Net.IPAddress.Loopback, 0));

        var app = builder.Build();

        // MapStaticAssets() registers the endpoint-based static assets pipeline.
        // StaticAssetDevelopmentRuntimeHandler (active in Development with a Build
        // manifest) resolves file content via IWebHostEnvironment.WebRootFileProvider,
        // NOT by direct filesystem probing. UseStaticWebAssets() (called above) has
        // already replaced WebRootFileProvider with a CompositeFileProvider that
        // layers in NuGet package asset paths (blazor.web.js, MudBlazor JS/CSS) and
        // the obj/scopedcss bundle, so all assets resolve correctly.
        //
        // We pass the explicit manifest path because ApplicationName in the test
        // runner context might not match "testhost" vs "PinballWizard.Web" discovery;
        // the explicit path removes any ambiguity.
        app.MapStaticAssets(manifestPath);

        // Antiforgery MUST come after authentication + authorization and before
        // the Blazor endpoint group (which carries antiforgery metadata).
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

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

    // Walk the test assembly's directory tree to find the built Web project
    // output. Both projects target net10.0 so they share the same TFM folder
    // under their respective bin trees.
    //
    // Layout (after `dotnet build`):
    //   <repo>/src/PinballWizard.Web/bin/Debug/net10.0/
    //       PinballWizard.Web.staticwebassets.endpoints.json  ← manifest
    //   <repo>/tests/PinballWizard.Web.Tests/bin/Debug/net10.0/
    //       PinballWizard.Web.Tests.dll                       ← test assembly (here)
    //
    // Probe heuristic: from the test assembly location, walk up until we reach
    // the repo root (presence of PinballWizard.slnx), then navigate to the
    // known Web output path.
    //
    // Returns: (contentRoot, manifestPath) — the content root directory and
    // the explicit manifest path to pass to MapStaticAssets(). The explicit
    // manifest path is required because when the test runner is named "testhost",
    // MapStaticAssets() defaults to "testhost.staticwebassets.endpoints.json"
    // which does not exist.
    private static (string contentRoot, string manifestPath) ResolveWebContentRoot()
    {
        var repoRoot = FindRepoRoot();

        // Prefer Debug; fall back to Release (CI may build Release).
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(
                repoRoot,
                "src", "PinballWizard.Web",
                "bin", config, "net10.0");

            var manifest = Path.Combine(candidate, "PinballWizard.Web.staticwebassets.endpoints.json");
            if (File.Exists(manifest))
                return (candidate, manifest);
        }

        throw new InvalidOperationException(
            "PinballWizard.Web.staticwebassets.endpoints.json not found. " +
            "Run `dotnet build src/PinballWizard.Web` before running Circuit tests.");
    }

    // Returns the source project's wwwroot: StaticAssetDevelopmentRuntimeHandler
    // (active during Build-manifest + non-Production serving) probes for asset
    // files at {WebRootPath}\{assetSubPath}. The bin output wwwroot is empty;
    // the source wwwroot contains the actual files (app.css, app.js, etc.).
    // NuGet-package-originated assets (_content/*, _framework/*) are supplied
    // by the UseStaticWebAssets() composite provider separately.
    private static string ResolveSourceWebRoot()
    {
        var repoRoot = FindRepoRoot();
        var sourceWebRoot = Path.Combine(repoRoot, "src", "PinballWizard.Web", "wwwroot");
        if (!Directory.Exists(sourceWebRoot))
            throw new InvalidOperationException(
                $"Source wwwroot not found at '{sourceWebRoot}'. " +
                "Ensure the repo checkout is complete.");
        return sourceWebRoot;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "PinballWizard.slnx")))
            current = current.Parent;

        if (current is null)
            throw new InvalidOperationException(
                "Cannot find repo root (PinballWizard.slnx). " +
                "Run `dotnet build src/PinballWizard.Web` before running Circuit tests.");

        return current.FullName;
    }

    // Returns 503 for all requests; used by stub HTTP clients so the wizard
    // clients gracefully fall back (admin pages don't use the wizard SSE path
    // but the DI registration requires a registered handler).
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
