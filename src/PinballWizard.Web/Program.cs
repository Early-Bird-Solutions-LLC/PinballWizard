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

using Azure.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
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

// ── Data Protection key ring (multi-replica ACA hosting) ──────────────────
// Blazor Server circuits + antiforgery tokens are encrypted with the Data
// Protection key ring. On Container Apps with >1 replica (or across
// restarts/deploys), the default ephemeral per-process key ring means a
// token minted by one replica cannot be decrypted by another — every
// circuit handshake fails and the app degrades to a dead prerender
// (observed live 2026-06-10: AntiforgeryValidationException / "key was
// not found in the key ring"). Persist the ring to blob storage and wrap
// it with a Key Vault key per the documented ACA setup
// (learn.microsoft.com/aspnet/core/blazor/host-and-deploy/server
// § Azure Container Apps). Works alongside ingress session affinity —
// affinity routes a live circuit to its owning replica; the shared ring
// keeps tokens valid across replicas and restarts.
//
// Gated on both URIs so local dev (no config) keeps the ephemeral ring.
// DefaultAzureCredential resolves the UAMI in ACA via AZURE_CLIENT_ID and
// the developer's az login locally.
var dpKeyRingBlobUri = builder.Configuration["DataProtection:KeyRingBlobUri"];
var dpKeyVaultKeyUri = builder.Configuration["DataProtection:KeyVaultKeyUri"];
if (!string.IsNullOrWhiteSpace(dpKeyRingBlobUri) && !string.IsNullOrWhiteSpace(dpKeyVaultKeyUri))
{
    builder.Services.AddDataProtection()
        .PersistKeysToAzureBlobStorage(new Uri(dpKeyRingBlobUri), new DefaultAzureCredential())
        .ProtectKeysWithAzureKeyVault(new Uri(dpKeyVaultKeyUri), new DefaultAzureCredential());
}

// ── Entra External ID auth scaffolding (ADR-0009) ─────────────────────────
// Auth is gated on a real TenantId being present. When TenantId is empty or
// the all-zeros placeholder (Dockerfile default), auth is skipped entirely —
// the zero-GUID causes OIDC metadata fetch failures that crash static-file
// requests. Set AzureAd:TenantId + AzureAd:ClientId in Key Vault / ACA env
// vars before shipping /admin routes.
var entraSection = builder.Configuration.GetSection("AzureAd");
var tenantId = entraSection["TenantId"] ?? string.Empty;
var isAuthConfigured = !string.IsNullOrWhiteSpace(tenantId)
    && tenantId != "00000000-0000-0000-0000-000000000000";

if (isAuthConfigured)
{
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(entraSection);

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
}
else
{
    // No real Entra tenant — register permissive auth so middleware compiles.
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();
    builder.Services.AddControllersWithViews();
}

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

// Serve .well-known/ explicitly — PhysicalFileProvider excludes dot-prefixed
// directories by default (ExclusionFilters.DotPrefixed), so a second middleware
// registration with ExclusionFilters.None is required.
// WebRootPath can be null when no wwwroot exists (e.g. CI environments without
// static assets); fall back to ContentRootPath/wwwroot and skip if absent.
var wellKnownPath = Path.Combine(
    builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
    ".well-known");
if (Directory.Exists(wellKnownPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(wellKnownPath, ExclusionFilters.None),
        RequestPath = "/.well-known",
        ServeUnknownFileTypes = true,
        DefaultContentType = "text/plain; charset=utf-8",
    });
}

app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// OTel default routes (/healthz + /alive) from ServiceDefaults.
app.MapDefaultEndpoints();

// AllowAnonymous so static assets (CSS, JS, fonts, Blazor framework files)
// are never caught by the fallback RequireAuthenticatedUser policy. Static
// files are not sensitive — they carry no user data and are public by design.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PinballWizard.Web.Client._Imports).Assembly);

// Microsoft.Identity.Web sign-in / sign-out controller routes.
app.MapControllers();

await app.RunAsync().ConfigureAwait(false);
