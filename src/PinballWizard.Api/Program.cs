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
using PinballWizard.Api.Middleware;
using PinballWizard.Application.Landing;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.Infrastructure.Landing;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// ── Aspire shared defaults ─────────────────────────────────────────────────
// OTel (logs / metrics / traces + OTLP exporter when env var present),
// service discovery, standard HTTP resilience, /healthz + /alive.
builder.AddServiceDefaults();

// ── RFC 9457 ProblemDetails (Wave 2 PR-D3) ───────────────────────────────
// IExceptionHandler implementation emits application/problem+json for all
// unhandled exceptions. Extensions: requestId (W3C trace ID), retryAfterSeconds
// (when applicable), timestampUtc. Stack traces NEVER leak to the user.
// AddProblemDetails registers IProblemDetailsService for the ASP.NET Core
// 8+ ProblemDetails pattern. UseExceptionHandler() (below) activates it.
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddProblemDetails();

// ── Foundry + AI Router (gated — mirrors CLI + Web wiring) ────────────────
// Gated on AiFoundry:ProjectEndpoint so the Api starts cleanly in local
// dev (Aspire emulator) where Foundry is not configured. When the endpoint
// IS configured, registers IFoundryAgentFactory, IAiRouter, and related
// services. The streaming endpoint returns 503 with Retry-After when the
// router is absent — see WizardAskStreamEndpoint.
// ── Landing service (PR-L1 / PR-L3) ──────────────────────────────────────────
// ILandingService (unconditional): seed questions + featured machines +
// system status composition. ISystemStatusProvider degrades gracefully to
// null fields when Foundry / AI Search / Cosmos are not configured.
builder.Services.AddLandingService();
builder.Services.AddSystemStatusProvider(builder.Configuration);

var foundryEndpoint = builder.Configuration["AiFoundry:ProjectEndpoint"];
var foundryWired = !string.IsNullOrWhiteSpace(foundryEndpoint);
if (foundryWired)
{
    builder.Services.AddAzureFoundryIntegration(builder.Configuration);
}

// ── Cosmos persistence (gated — mirrors CLI Program.cs wiring) ────────────
// The Wizard's getMachineByTitle grounding tool (MachineGroundingTool) depends
// on IMachineRepository, which AddCosmosPersistence registers. AddAiRouter
// registers MachineGroundingTool as a singleton, so without this the router's
// tool graph fails to resolve the first time a question is asked. Gated on
// Cosmos:AccountEndpoint; AddCosmosPersistence builds a Managed-Identity
// CosmosClient from that endpoint (deployed account and local Phase-0 runs
// both use this path — the Api host has no Aspire Cosmos client dependency,
// unlike the Cli which also supports the loopback emulator connection string).
// Absent in unit tests / clean local dev — the Api starts without Cosmos.
if (!string.IsNullOrWhiteSpace(builder.Configuration[CosmosOptions.AccountEndpointKey]))
{
    builder.Services.AddCosmosPersistence(builder.Configuration);
}

// ── Azure AI Search (gated — mirrors CLI Program.cs wiring) ───────────────
// The Wizard's searchCorpus tool (SearchCorpusTool) depends on IRagRetriever,
// which AddAzureAiSearchIntegration registers. AddAiRouter registers
// SearchCorpusTool as a singleton, so without this the router's tool graph
// fails to resolve. Gated on AiSearch:Endpoint; the retriever's IQueryEmbedder
// additionally requires AiFoundry:ProjectEndpoint (already wired above when
// present). Absent in local dev before the AI Search hand-off — start clean.
if (!string.IsNullOrWhiteSpace(builder.Configuration[AiSearchOptions.EndpointKey]))
{
    builder.Services.AddAzureAiSearchIntegration(builder.Configuration);
}

var app = builder.Build();

// ── Boot-duration instrumentation (#361) ──────────────────────────────────
// The 2026-06-11 incident measured ~2.5-3 min from container start to first
// listen under KEDA scale-from-zero wake; under minReplicas=1 (#360) boots
// are ~1s and the pathology no longer reproduces. This single line is the
// witness either way: process-start → ApplicationStarted elapsed, in every
// boot's logs. If scale-from-zero ever returns and the number balloons,
// the breakdown question ("where did the time go?") starts answered.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var elapsed = DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    app.Logger.LogInformation(
        "api.boot_duration_ms={BootMs:F0} (process start -> ApplicationStarted)",
        elapsed.TotalMilliseconds);
});

// ── Exception handler ─────────────────────────────────────────────────────
// Must be placed BEFORE other middleware so unhandled exceptions from any
// downstream middleware or endpoint are caught and returned as RFC 9457
// application/problem+json. UseExceptionHandler() activates the registered
// IExceptionHandler chain (ProblemDetailsExceptionHandler).
app.UseExceptionHandler();

// OTel default routes (/healthz + /alive) from ServiceDefaults.
app.MapDefaultEndpoints();

// Wave 1 PR-F2: POST /api/wizard/ask:stream (SSE)
app.MapWizardStreamingEndpoints();

// Wave 2 PR-L3: GET /api/wizard/landing
app.MapWizardLandingEndpoint();

await app.RunAsync().ConfigureAwait(false);
