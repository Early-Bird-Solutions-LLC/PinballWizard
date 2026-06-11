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
using Polly;
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
    var dpCredential = new DefaultAzureCredential();
    builder.Services.AddDataProtection()
        .PersistKeysToAzureBlobStorage(new Uri(dpKeyRingBlobUri), dpCredential)
        .ProtectKeysWithAzureKeyVault(new Uri(dpKeyVaultKeyUri), dpCredential);
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
    // Stacked standard pipelines (this + the ServiceDefaults one) are fine
    // HERE, unlike the streaming client below: /api/wizard/landing is an
    // idempotent, cheap GET — retries are safe and the 5s HttpClient.Timeout
    // above is the effective bound. The asymmetry is deliberate.
    .AddStandardResilienceHandler();

// ── Wizard SSE streaming client ───────────────────────────────────────────
// Typed HttpClient that connects to PinballWizard.Api's SSE endpoint.
// Base address uses Aspire service-discovery notation ("https+http://pinwiz-api")
// so Aspire injects the correct host in local dev and ACA injects the
// internal FQDN in Azure.
//
// Resilience is deliberately NOT the stock pipeline (2026-06-11 incident):
// ask:stream is a long-lived SSE response to a NON-IDEMPOTENT request —
// every send triggers a full multi-agent LLM run on the Api. The stacked
// defaults (ServiceDefaults' 50s-attempt/120s-total pipeline plus a bare
// per-client standard handler at 10s/30s) retried that request on attempt
// timeout, re-running whole agent runs in a feedback storm while the user
// saw "Wizard is thinking" forever. The client sends with
// ResponseHeadersRead and the Api flushes an SSE preamble at accept, so a
// pipeline attempt now completes at headers (~network RTT) — timeouts below
// bound CONNECTION+HEADERS only, never model latency.
//
// EXTEXP0001 suppressed for this statement: RemoveAllResilienceHandlers is
// marked experimental, but it is the supported opt-out from
// ConfigureHttpClientDefaults pipelines — the alternative (two stacked
// retrying pipelines around a non-idempotent LLM call) is precisely the
// 2026-06-11 incident. The compiler reports the diagnostic at the statement
// head, so the pragma wraps the full fluent chain.
#pragma warning disable EXTEXP0001
builder.Services
    .AddHttpClient<IWizardStreamingClient, WizardStreamingClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://pinwiz-api");
        // The stream outlives any fixed request timeout (agent answers run
        // 10s–2min+); HttpClient.Timeout would also cancel mid-stream body
        // reads. The component owns the user-facing budget via its
        // CancellationToken; the server always terminates with final+end.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    // Drop the ServiceDefaults pipeline for this client — its retries are
    // unsafe here (see above) and its 120s total was the "00:02:00" failure
    // users saw. Replaced wholesale by the handler below.
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(options =>
    {
        // Never retry: a duplicate attempt is a duplicate LLM agent run
        // (cost + latency + Api saturation). The WizardAnswerStream
        // component owns the single deliberate fallback re-attempt.
        options.Retry.ShouldHandle = _ => PredicateResult.False();
        // Headers arrive at request-accept (Api preamble), so this bounds
        // routing + TLS + dispatch + preamble — never the model. Measured
        // basis: warm-path header arrival is sub-second (the deploy smoke's
        // /alive RTT and the canary's ask both confirm), but during the
        // 2026-06-11 incident a CPU-saturated Api delayed request dispatch
        // by multiple seconds; 15s rides out that observed worst case
        // without re-introducing the wait-on-model failure mode.
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
        // With ResponseHeadersRead the pipeline completes at headers; the
        // total only matters when the connection itself wedges pre-headers.
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    });
#pragma warning restore EXTEXP0001

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
