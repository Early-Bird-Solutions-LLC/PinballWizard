// PinballWizard.Web — Blazor Web App (auto-render mode, Server + WASM)
//
// Wave 1 PR-F0 skeleton (amended). Establishes the project, DI wiring,
// ServiceDefaults (OTel + healthz + alive), Entra External ID auth
// scaffolding (AzureAd section intentionally empty — real tenant
// config arrives in a follow-up PR), and the empty /wizard route.
//
// Auto-render mode wiring: AddInteractiveWebAssemblyComponents +
// AddInteractiveWebAssemblyRenderMode + AddAdditionalAssemblies are
// fully wired here — PinballWizard.Web.Client is the WASM runtime.
// Components using @rendermode InteractiveAuto start Server-side
// (instant TTFB) and migrate to WASM after the runtime downloads.
//
// PR-F1 layers the WizardShell + MainLayout + MudBlazor theme tokens.
// PR-F2 layers the /api/wizard/ask:stream SSE endpoint + Api project.
//
// ADR-0026 § 1 — Blazor Web App with auto-render mode (Server + WASM)
// ADR-0009    — Entra External ID auth (configured in a follow-up PR)
// ADR-0008    — MudBlazor strict for all chrome

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;
using PinballWizard.Application.Ai.Hosting;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.ServiceDefaults;
using PinballWizard.Web.Clients;
using PinballWizard.Web.Components;
using PinballWizard.Web.Components.Degraded;
using PinballWizard.Web.Components.Wizard;
using PinballWizard.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Aspire shared defaults ─────────────────────────────────────────────────
// OTel (logs / metrics / traces + OTLP exporter when env var present),
// service discovery, standard HTTP resilience, /healthz + /alive.
builder.AddServiceDefaults();

// ── Razor components (Blazor Web App, auto-render mode per ADR-0026 § 1) ──
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// ── MudBlazor (ADR-0008 — sole chrome library) ────────────────────────────
builder.Services.AddMudServices();

// ── Entra External ID auth scaffolding (ADR-0009) ─────────────────────────
// AzureAd section is intentionally empty in this Wave 1 skeleton.
// Authentication is required for /admin routes; public routes
// (/wizard, /, /about, /status, /error) carry [AllowAnonymous].
//
// FOLLOW-UP: set AzureAd:TenantId, AzureAd:ClientId, and related
// fields in appsettings.json or Key Vault before shipping admin routes.
// Until then, the builder is registered so the wiring compiles and
// the auth middleware pipeline is structurally correct.
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Blanket authorization policy: every route requires authentication by default.
// Public routes (/wizard, /, /about, /settings, /status, /error, /tilt, /{**slug})
// opt out with [AllowAnonymous]. Admin routes (/admin/**) are protected automatically
// without needing per-page [Authorize] attributes — new admin pages are secure by
// default and cannot be accidentally left open. The API minimal-API endpoints
// (/api/wizard/ask:stream, /api/wizard/landing) and health check endpoints
// (/healthz, /alive) carry explicit .AllowAnonymous() in their registrations.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

// ── Degradation state store (scoped per circuit) ──────────────────────────
// IClientDegradationStore propagates DegradationContext from WizardAnswer
// responses to OutageBanner without requiring a global singleton or
// cascading value. Scoped lifetime = one instance per Blazor Server circuit.
// ADR-0026 § 5 — graceful degradation surface.
builder.Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();

// ── User preferences (theme / motion / sound — localStorage via JS interop) ─
// Scoped so each Blazor Server circuit has its own initialized state.
// ADR-0026 — sound muted by default; ADR-0027 — no captive UI.
builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();

// ── Landing page client ────────────────────────────────────────────────────
// Typed HttpClient for GET /api/wizard/landing. Returns null on non-200 so
// the Index page renders its compiled-in fallback — the prospect's first
// impression MUST NOT 500 if the endpoint is unavailable.
// ADR-0026 § Landing surface.
builder.Services
    .AddHttpClient<IWizardLandingClient, WizardLandingClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://pinwiz-api");
        // 5-second timeout for the landing call — fast cold-start budget.
        // If the endpoint doesn't respond in 5s, the compiled-in fallback
        // renders so the prospect doesn't wait for a broken dependency.
        client.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddStandardResilienceHandler();

// ── Wizard SSE streaming client ───────────────────────────────────────────
// Typed HttpClient that connects to PinballWizard.Api's SSE endpoint.
// Base address uses Aspire service-discovery notation ("https+http://pinwiz-api")
// so Aspire injects the correct host in local dev and ACA injects the
// internal FQDN in Azure. AddStandardResilienceHandler adds standard
// retry + circuit-breaker from Microsoft.Extensions.Http.Resilience.
builder.Services
    .AddHttpClient<IWizardStreamingClient, WizardStreamingClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://pinwiz-api");
        // SSE streams can run for seconds; the default 100s timeout is fine
        // for Wave 1 one-shot answers. Wave 2 PR-S2 (RunStreamingAsync) may
        // need an infinite timeout — document at that point.
    })
    .AddStandardResilienceHandler();

// ── Foundry + AI Router (gated — mirrors CLI wiring) ──────────────────────
// Gated on AiFoundry:ProjectEndpoint so the Web project starts cleanly
// in local dev (Aspire emulator) where Foundry is not configured.
// When the endpoint IS configured, registers IFoundryAgentFactory,
// IAiRouter, and the WizardAgentWarmupHostedService so the first
// user request doesn't pay the Foundry handshake cost.
var foundryEndpoint = builder.Configuration["AiFoundry:ProjectEndpoint"];
var foundryWired = !string.IsNullOrWhiteSpace(foundryEndpoint);
if (foundryWired)
{
    builder.Services.AddAzureFoundryIntegration(builder.Configuration);
    builder.Services.AddHostedService<WizardAgentWarmupHostedService>();
}

// ── Build + pipeline ───────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    // Per ADR-0026 § 5 — pinball-themed /error page for unhandled exceptions.
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}
// UseHttpsRedirection and UseHsts are intentionally omitted.
// PinballWizard runs in Azure Container Apps where the Azure-provided load
// balancer terminates TLS and forwards plain HTTP to the container. Adding
// an in-app HTTPS redirect causes a redirect loop (the container only speaks
// HTTP; the LB already enforces HTTPS at the edge). HSTS is likewise the
// LB's responsibility via the Azure Front Door / Application Gateway layer.
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// OTel default routes (/healthz + /alive) from ServiceDefaults.
app.MapDefaultEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PinballWizard.Web.Client._Imports).Assembly);

// Microsoft.Identity.Web sign-in / sign-out controller routes.
app.MapControllers();

await app.RunAsync().ConfigureAwait(false);
