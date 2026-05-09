// PinballWizard.Api — JSON / SSE host per ADR-0026 § 1.
//
// This project is the public API surface consumed by PinballWizard.Web and,
// in future, external tooling. Keeping it separate from the Blazor Web project
// means the Razor SDK never enters the Api compilation unit, and the two
// services can scale independently as ACA replicas or sit behind different
// edge-cache rules.
//
// Wave 1 PR-F2 ships:
//   POST /api/wizard/ask:stream — Server-Sent Events, AnswerChunk-shaped JSON
//
// Wave 2 PR-D3 layers RFC 9457 ProblemDetails middleware + IExceptionHandler.
// Wave 2 PR-L3 adds GET /api/wizard/landing.
// Wave 2 PR-S2/S3 swap the streaming impl to live RunStreamingAsync deltas.
//
// ADR-0026 § 1 — separate Api project + Blazor Web App
// ADR-0026 § 2 — SSE transport (not SignalR, not WebSocket)
// ADR-0026 § 4 — AnswerChunk discriminated union on the wire

using PinballWizard.Api.Endpoints;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// ── Aspire shared defaults ─────────────────────────────────────────────────
// OTel (logs / metrics / traces + OTLP exporter when env var present),
// service discovery, standard HTTP resilience, /healthz + /alive.
builder.AddServiceDefaults();

// ── Foundry + AI Router (gated — mirrors CLI + Web wiring) ────────────────
// Gated on AiFoundry:ProjectEndpoint so the Api starts cleanly in local
// dev (Aspire emulator) where Foundry is not configured. When the endpoint
// IS configured, registers IFoundryAgentFactory, IAiRouter, and related
// services. The streaming endpoint returns 503 with Retry-After when the
// router is absent — see WizardAskStreamEndpoint.
var foundryEndpoint = builder.Configuration["AiFoundry:ProjectEndpoint"];
var foundryWired = !string.IsNullOrWhiteSpace(foundryEndpoint);
if (foundryWired)
{
    builder.Services.AddAzureFoundryIntegration(builder.Configuration);
}

var app = builder.Build();

// OTel default routes (/healthz + /alive) from ServiceDefaults.
app.MapDefaultEndpoints();

// Wave 1 PR-F2: POST /api/wizard/ask:stream (SSE)
app.MapWizardStreamingEndpoints();

await app.RunAsync().ConfigureAwait(false);
