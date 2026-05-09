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
using PinballWizard.Web.Components;
using PinballWizard.Web.Components.Wizard;

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

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

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
    // ProblemDetails middleware and TiltErrorBoundary land in PR-D3 (Wave 2).
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
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
