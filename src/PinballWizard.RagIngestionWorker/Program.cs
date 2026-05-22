using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Ingestion;
using PinballWizard.ServiceDefaults;

// W3-2 RAG ingestion worker — Container-App-hosted Cosmos Change Feed
// consumer. Reads `scraped_documents` change feed, runs the Application-
// layer ingestion pipeline (extract → chunk → embed → AI Search upsert),
// and persists state + dead-letters to dedicated Cosmos containers.
//
// Hosting: Azure Container Apps with the KEDA Cosmos scaler (per
// `memory/feedback_compute_on_container_apps.md`). NOT a standalone
// Functions App. Image swap path is the standard ACA `update --image`
// flow once an operator pushes the worker image to ACR (per Phase 4
// W3-2 operator hand-off in the session handoff).
//
// Configuration: every option key is environment-variable-bindable
// (Bicep wires `Cosmos__AccountEndpoint`, `AiSearch__Endpoint`,
// `AiFoundry__ProjectEndpoint`, etc. on the Container App's `env`
// block). DI gates each integration on the presence of its primary
// config key so a half-configured deploy fails fast at startup
// rather than midway through the first change-feed batch.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Cosmos persistence — required for the worker to function at all.
// Both the source (`scraped_documents`) + lease + state + dead-letter
// containers all live in the configured Cosmos account.
builder.Services.AddCosmosPersistence(builder.Configuration);

// Foundry + AI Search integrations — required for the indexer's
// embedding + search-upload calls.
builder.Services.AddAzureFoundryIntegration(builder.Configuration);
builder.Services.AddAzureAiSearchIntegration(builder.Configuration);

// Application-layer chunker + Infrastructure-layer extractor — the
// pipeline depends on both.
builder.Services.AddHybridChunker();
builder.Services.AddPdfDocumentTextExtractor(builder.Configuration);

// Application-layer pipeline orchestrator.
builder.Services.AddRagIngestionPipeline();

// Infrastructure-layer change-feed consumer — registers the hosted
// service (BackgroundService) that drives the actual Change Feed
// processor + per-document handler routing + dead-letter sink.
builder.Services.AddCosmosChangeFeedRagIngestion(builder.Configuration);

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
