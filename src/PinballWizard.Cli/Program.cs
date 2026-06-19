using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using PinballWizard.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Landing;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Documents;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Integrations.PinballMap;
using PinballWizard.Infrastructure.Catalog;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Application.Rag.MetadataCards;
using PinballWizard.Infrastructure.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Indexing;
using PinballWizard.Infrastructure.Rag.Ingestion;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Jjp;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Infrastructure.Scraping.BarrelsOfFun;
using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using PinballWizard.Infrastructure.Scraping.Multimorphic;
using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Spooky;
using PinballWizard.Infrastructure.Scraping.Stern;
using PinballWizard.ServiceDefaults;
using Polly;
using Polly.Retry;

// ── CLI Definition ────────────────────────────────────────────────────────────

var sourceOption = new Option<string?>("--source", "-s")
{
    Description = "Which source(s) to scrape: manuals, games, bulletins, jjp, ap, spooky, pinballbrothers, barrelsoffun, cgc, multimorphic, opdb, all. " +
                  "NOTE: 'all' runs every ISourceScraper but does NOT include 'opdb' — OPDB writes to IMachineRepository instead of yielding ScrapedItems and is special-cased; run --source opdb explicitly to sync the OPDB catalog.",
    DefaultValueFactory = _ => "all"
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Scrape but don't persist changes"
};

var installPlaywrightOption = new Option<bool>("--install-playwright")
{
    Description = "Install Playwright browsers and exit"
};

var ensureCosmosContainersOption = new Option<bool>("--ensure-cosmos-containers")
{
    Description = "Run CosmosBootstrapper.EnsureCreatedAsync against the configured Cosmos account: creates the database + every container in CosmosOptions.Containers if missing, asserts partition-key paths match. Idempotent. Useful as a post-deploy smoke-test that the configured Cosmos endpoint + Managed Identity / Aspire connection string actually work end-to-end. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var seedIngestionSourcesOption = new Option<bool>("--seed-ingestion-sources")
{
    Description = "Read data/seeds/ingestion_sources.v1.json (relative to the current working directory, typically the repo root) and upsert each entry into the Cosmos ingestion_sources container. Idempotent: re-runs apply config field changes from the manifest while preserving runtime fields (LastRunAt, LastSuccessAt, totalDocumentsDiscovered, totalRunFailures) populated by actual scraper runs. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var seedFeaturedMachinesOption = new Option<bool>("--seed-featured-machines")
{
    Description = "Read data/seeds/featured_machines.v1.json (relative to the current working directory, typically the repo root) and upsert each entry into the Cosmos featured_machines container. Idempotent: re-runs apply content changes from the manifest (showcase copy, display_order edits) without data loss. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var ensureAzureFoundryOption = new Option<bool>("--ensure-azure-foundry")
{
    Description = "Post-deploy smoke-test for the Azure AI Foundry project (ADR-0014): connects via DefaultAzureCredential, enumerates model deployments, asserts the configured chat (AiFoundry:ChatDeploymentName) and embedding (AiFoundry:EmbeddingDeploymentName) deployments are present. Idempotent. Requires AiFoundry:ProjectEndpoint to be configured. Exit code 2 + remediation hint when not configured or the smoke probe fails."
};

var ensureAiSearchOption = new Option<bool>("--ensure-ai-search")
{
    Description = "Post-deploy smoke-test for the Azure AI Search index backing Phase 4 RAG retrieval (ADR-0021): connects via DefaultAzureCredential, calls GetDocumentCount on the configured index (AiSearch:IndexName, default pinwiz-rag-v1) to confirm endpoint reachability + AAD auth + that the index is queryable with the Search Index Data Reader role. The index is expected to exist (W2-3 created it). Idempotent. Requires AiSearch:Endpoint to be configured. Exit code 2 + remediation hint when not configured or the smoke probe fails."
};

var ensureRagIndexOption = new Option<bool>("--ensure-rag-index")
{
    Description = "Idempotently creates the Phase 4 RAG index (ADR-0021, default name `pinwiz-rag-v1`) on the configured Azure AI Search service if it does not yet exist. No-op when the index is already present — schema mutations follow the v1→v2 cutover documented in ADR-0021 § Versioning strategy, not in-place updates. Requires AiSearch:Endpoint to be configured. Exit code 2 + remediation hint when not configured or the create call fails."
};

var rebuildRagIndexOption = new Option<bool>("--rebuild-rag-index")
{
    Description = "DESTRUCTIVE: drops the RAG index entirely and recreates it empty from the current schema, then exits. Removes ALL indexed chunks — you must re-ingest afterwards (--run-rag-backfill + --sync-metadata-cards). This is the supported way to correct mislabeled chunks (e.g. after the linker re-link): the index is a rebuildable projection of Cosmos, so corrections happen by wipe-and-rebuild, never by per-chunk deletion. Requires AiSearch:Endpoint to be configured."
};

var askOption = new Option<string?>("--ask")
{
    Description = "Phase 3 thin Wizard slice: invokes the IAiRouter end-to-end against the deployed Foundry project (per ADR-0014) for a single question and prints the WizardAnswer JSON. Requires AiFoundry:ProjectEndpoint to be configured. Wave 2 PR 4 ships the skeleton (Wizard agent only); PR 5 adds sub-agents + getMachineByTitle grounding; PR 6 adds confidence-driven refusal."
};

var evalOption = new Option<bool>("--eval")
{
    Description = "Phase 3 evaluation harness (ADR-0016): drives every question in data/eval/wizard.v1.jsonl through IAiRouter, scores responses with the four custom code-based evaluators (citation precision/recall, subagent accuracy, refusal correctness), and writes a timestamped JSON file to data/eval/results/. Idempotently registers the evaluator definitions with the Foundry project so they are surfaced in the portal alongside built-ins. Requires AiFoundry:ProjectEndpoint to be configured."
};

var runRagBackfillOption = new Option<bool>("--run-rag-backfill")
{
    Description = "One-shot RAG index backfill: iterates all scraped_documents via the raw Change Feed stream iterator (no lease checkpoints) and runs each document through the full RAG ingestion pipeline (extract → chunk → embed → AI Search upsert). Idempotent: documents already indexed with the same content hash are skipped. Use after provisioning a fresh RAG index to populate it from existing scraped documents before the Change Feed Processor starts handling ongoing writes. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured."
};

var syncMetadataCardsOption = new Option<bool>("--sync-metadata-cards")
{
    Description = "Synthesize metadata_card chunks from the Cosmos machines container and upsert them into AI Search. One card per machine record — covers title, manufacturer, year, designers, themes, editions, and MSRP. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured."
};

var linkDocumentsOption = new Option<bool>("--link-documents")
{
    Description = "Run the document-to-machine linker: processes all pending, failed, and not_in_catalog records in scraped_documents_raw through the 5-tier algorithm (override → xref slug → filename → page 1 → page 2) and fan-outs resolved documents into scraped_documents. Idempotent: already-terminal records (Linked, ManuallyLinked, PlatformGeneric) are skipped. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var relinkAllOption = new Option<bool>("--relink-all")
{
    Description = "Re-run the linker over ALL previously-linked documents: first resets every Linked / NotInCatalog record in scraped_documents_raw back to Pending (preserving ManuallyLinked admin overrides and PlatformGeneric), then runs the standard --link-documents pass. Use after the linker logic changes (e.g. the manufacturer-disambiguation fix) so existing mislabeled links are re-resolved. Implies --link-documents. Requires Cosmos to be configured."
};

var downloadDocumentsOption = new Option<bool>("--download-documents")
{
    Description = "Download every not-yet-downloaded document in scraped_documents_raw to the local downloads root so the linker's page-text tiers (Tier 3/4) can read page-1 content for edition resolution. Polite (throttled, robots-honored) and idempotent (documents with a local file are skipped). Combine with --force-redownload to re-download even documents already recorded as downloaded (edition_scope backfill). Run before --link-documents / --relink-all when page-1 content is needed. Requires Cosmos to be configured."
};

var forceRedownloadOption = new Option<bool>("--force-redownload")
{
    Description = "Modifier for --download-documents: re-download EVERY document even if its raw record already records a file.local_path, using an unconditional GET (ignores stored ETag/Last-Modified). Use when the recorded LocalPath points at a file from an earlier ephemeral run (e.g. an ACA job's /tmp) that is not present on this machine, so the linker's page-1 edition tier has the bytes to read. Still fully polite (every request routes through the politeness gate). Intended for the edition_scope backfill: --download-documents --force-redownload, then --relink-all."
};

var downloadAndLinkOption = new Option<bool>("--download-and-link")
{
    Description = "Combined nightly verb: first downloads not-yet-downloaded documents to blob storage (Storage:BlobEndpoint / pinwiz-raw container; polite, idempotent), then runs the document-to-machine linker so it has page-1 content for edition resolution. Equivalent to --download-documents followed by --link-documents in a single invocation. Respects --force-redownload. Exit code 1 if either stage has failures; exit code 2 if Cosmos is not configured. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var migrateDownloadPathsOption = new Option<bool>("--migrate-download-paths")
{
    Description = "One-shot, byte-safe migration: corrects legacy already-rooted scraped_documents_raw file.local_path values (e.g. 'data/downloads/manualspage/x.pdf', written by the pre-fix downloader) to the clean relative form ('manualspage/x.pdf'). For each affected row it verifies the on-disk file's SHA-256 matches the recorded hash (refusing to migrate a mismatch), moves the file to the correct single location, and rewrites local_path. Idempotent (already-relative rows are skipped). Combine with --dry-run to report what would change without moving files or writing Cosmos. Requires Cosmos to be configured."
};

var rebuildCatalogStatsOption = new Option<bool>("--rebuild-catalog-stats")
{
    Description = "Recomputes every per-manufacturer catalog_stats rollup from scratch: streams all machines, reads each machine's scraped_documents (single-partition), aggregates doc counts and type distribution, and upserts the authoritative per-manufacturer rollup document. This is the rebuildable-projection backstop (ADR-0031/ADR-0036) — also the only path that sets authoritative identity fields (EditionLabel, GroupId, Year, IsOpdbOnly) on each MachineStatEntry from the live Machine record. Idempotent. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var rootCommand = new RootCommand("PinballWizard — Stern Pinball content scraper");
rootCommand.Options.Add(sourceOption);
rootCommand.Options.Add(dryRunOption);
rootCommand.Options.Add(installPlaywrightOption);
rootCommand.Options.Add(ensureCosmosContainersOption);
rootCommand.Options.Add(seedIngestionSourcesOption);
rootCommand.Options.Add(seedFeaturedMachinesOption);
rootCommand.Options.Add(ensureAzureFoundryOption);
rootCommand.Options.Add(ensureAiSearchOption);
rootCommand.Options.Add(ensureRagIndexOption);
rootCommand.Options.Add(rebuildRagIndexOption);
rootCommand.Options.Add(askOption);
rootCommand.Options.Add(evalOption);
rootCommand.Options.Add(runRagBackfillOption);
rootCommand.Options.Add(syncMetadataCardsOption);
rootCommand.Options.Add(linkDocumentsOption);
rootCommand.Options.Add(relinkAllOption);
rootCommand.Options.Add(downloadDocumentsOption);
rootCommand.Options.Add(downloadAndLinkOption);
rootCommand.Options.Add(forceRedownloadOption);
rootCommand.Options.Add(migrateDownloadPathsOption);
rootCommand.Options.Add(rebuildCatalogStatsOption);

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var source = parseResult.GetValue(sourceOption);
    var dryRun = parseResult.GetValue(dryRunOption);
    var installPw = parseResult.GetValue(installPlaywrightOption);
    var ensureCosmos = parseResult.GetValue(ensureCosmosContainersOption);
    var seedIngestionSources = parseResult.GetValue(seedIngestionSourcesOption);
    var seedFeaturedMachines = parseResult.GetValue(seedFeaturedMachinesOption);
    var ensureAzureFoundry = parseResult.GetValue(ensureAzureFoundryOption);
    var ensureAiSearch = parseResult.GetValue(ensureAiSearchOption);
    var ensureRagIndex = parseResult.GetValue(ensureRagIndexOption);
    var rebuildRagIndex = parseResult.GetValue(rebuildRagIndexOption);
    var ask = parseResult.GetValue(askOption);
    var eval = parseResult.GetValue(evalOption);
    var runRagBackfill = parseResult.GetValue(runRagBackfillOption);
    var syncMetadataCards = parseResult.GetValue(syncMetadataCardsOption);
    var linkDocuments = parseResult.GetValue(linkDocumentsOption);
    var relinkAll = parseResult.GetValue(relinkAllOption);
    var downloadDocuments = parseResult.GetValue(downloadDocumentsOption);
    var downloadAndLink = parseResult.GetValue(downloadAndLinkOption);
    var forceRedownload = parseResult.GetValue(forceRedownloadOption);
    var migrateDownloadPaths = parseResult.GetValue(migrateDownloadPathsOption);
    var rebuildCatalogStats  = parseResult.GetValue(rebuildCatalogStatsOption);

    // Handle --install-playwright
    if (installPw)
    {
        Console.WriteLine("Installing Playwright browsers...");
        PlaywrightFactory.InstallBrowsers();
        Console.WriteLine("Playwright browsers installed successfully.");
        return;
    }

    // Build host with DI
    using var host = CreateHost(args);

    // ScraperOrchestrator is only registered when AddCosmosPersistence was wired
    // (i.e., Cosmos config is present). GetService<T>() returns null rather than
    // throwing an opaque DI exception; the null-check below surfaces a friendly
    // remediation message instead.
    var orchestrator = host.Services.GetService<ScraperOrchestrator>();
    if (orchestrator is null)
    {
        Console.Error.WriteLine(
            "Scraping requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
            "(Aspire) or Cosmos:AccountEndpoint (production) in appsettings or environment, " +
            "then re-run. See docs/adr/0012-cosmos-arm-schema-data-plane-items.md for setup.");
        Environment.ExitCode = 2;
        return;
    }

    // Handle --ensure-cosmos-containers (post-deploy Cosmos smoke-test).
    // Resolves CosmosBootstrapper from DI; the bootstrapper is only registered
    // when AddCosmosPersistence was wired (i.e., Cosmos config is present). A
    // missing service indicates Cosmos is not configured — exit code 2 with a
    // remediation message rather than an opaque DI failure.
    if (ensureCosmos)
    {
        var bootstrapper = host.Services.GetService<CosmosBootstrapper>();
        if (bootstrapper is null)
        {
            Console.Error.WriteLine(
                "--ensure-cosmos-containers requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        await bootstrapper.EnsureCreatedAsync(cancellationToken);
        Console.WriteLine("Cosmos database + containers ensured.");
        return;
    }

    // Handle --seed-ingestion-sources (one-shot bootstrap for the
    // ingestion_sources Cosmos container). Resolves IIngestionSourceSeeder
    // from DI; the seeder is only registered when AddCosmosPersistence was
    // wired (i.e., Cosmos config is present). Mirrors the
    // --ensure-cosmos-containers exit-code-2 remediation pattern.
    if (seedIngestionSources)
    {
        var seeder = host.Services.GetService<IIngestionSourceSeeder>();
        if (seeder is null)
        {
            Console.Error.WriteLine(
                "--seed-ingestion-sources requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        var manifestPath = Path.Combine("data", "seeds", "ingestion_sources.v1.json");
        var seedResult = await seeder.SeedAsync(manifestPath, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"Ingestion sources seeded: {seedResult.Inserted} inserted, " +
                          $"{seedResult.Updated} updated, {seedResult.Total} total.");
        return;
    }

    // Handle --seed-featured-machines (one-shot bootstrap for the
    // featured_machines Cosmos container). Resolves IFeaturedMachineSeedLoader
    // and IFeaturedMachineRepository from DI; both are only registered when
    // AddCosmosPersistence was wired (i.e., Cosmos config is present) and
    // AddLandingService was called. Mirrors the --seed-ingestion-sources
    // exit-code-2 remediation pattern.
    if (seedFeaturedMachines)
    {
        var loader = host.Services.GetService<IFeaturedMachineSeedLoader>();
        var repo = host.Services.GetService<IFeaturedMachineRepository>();
        if (loader is null || repo is null)
        {
            Console.Error.WriteLine(
                "--seed-featured-machines requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        var documents = await loader.LoadAsync(cancellationToken);
        var upserted = 0;
        foreach (var doc in documents)
        {
            await repo.UpsertAsync(doc, cancellationToken);
            upserted++;
            Console.WriteLine($"  Upserted: {doc.Id} (display_order={doc.DisplayOrder})");
        }

        Console.WriteLine();
        Console.WriteLine($"Featured machines seeded: {upserted} upserted.");
        return;
    }

    // Handle --run-rag-backfill (one-shot RAG index population of all
    // eligible scraped_documents). Resolves IRagBackfillService
    // from DI; only registered when Cosmos + AI Search + AI Foundry are
    // all configured. Mirrors the --ensure-cosmos-containers exit-code-2
    // remediation pattern.
    if (runRagBackfill)
    {
        var backfill = host.Services.GetService<IRagBackfillService>();
        if (backfill is null)
        {
            Console.Error.WriteLine(
                "--run-rag-backfill requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("RAG backfill starting — this may take several minutes depending on corpus size...");
        var result = await backfill.RunAsync(cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"RAG backfill complete: {result.Processed} processed, " +
                          $"{result.Indexed} indexed, {result.Skipped} skipped, " +
                          $"{result.Failed} failed, duration {result.Duration.TotalSeconds:N1}s");
        if (result.Failed > 0)
        {
            Console.Error.WriteLine($"  {result.Failed} documents failed — check logs for details.");
            Environment.ExitCode = 1;
        }
        return;
    }

    // Handle --sync-metadata-cards (Phase 4.5 W3a — synthesize one metadata_card
    // chunk per Cosmos machine record and upsert into AI Search). Gated on the
    // same three backend services as --run-rag-backfill. Idempotent: re-running
    // overwrites in-place (AI Search upsert semantics; chunk_id is a hash of the
    // machine+document+position key computed by AiSearchRagIndexer.ComputeChunkId).
    if (syncMetadataCards)
    {
        var machineRepo = host.Services.GetService<IMachineRepository>();
        var synthesizer = host.Services.GetService<IMetadataCardSynthesizer>();
        var indexer = host.Services.GetService<IRagIndexer>();

        if (machineRepo is null || synthesizer is null || indexer is null)
        {
            Console.Error.WriteLine(
                "--sync-metadata-cards requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("Synthesizing metadata cards from Cosmos machines...");

        var upserted = 0;
        var failed = 0;
        var indexerOptions = new RagIndexerOptions();

        string[] allManufacturers =
        [
            ScraperManufacturerKey.Stern,
            ScraperManufacturerKey.Jjp,
            ScraperManufacturerKey.AmericanPinball,
            ScraperManufacturerKey.Spooky,
            ScraperManufacturerKey.PinballBrothers,
            ScraperManufacturerKey.BarrelsOfFun,
            ScraperManufacturerKey.ChicagoGaming,
            ScraperManufacturerKey.Multimorphic,
        ];

        foreach (var manufacturer in allManufacturers)
        {
            await foreach (var machine in machineRepo.StreamByManufacturerAsync(manufacturer, cancellationToken))
            {
                var chunk = synthesizer.Synthesize(machine);
                var chunkRequest = new ChunkRequest(
                    MachineId: machine.Id,
                    MachineTitle: machine.Title,
                    Manufacturer: machine.ManufacturerDisplayName,
                    DocumentId: $"meta_{machine.Id}",
                    DocumentUrl: machine.OpdbSourceUrl ?? OpdbMachineMapper.OpdbWebUrl(machine.Id),
                    DocumentType: DocumentType.MetadataCard,
                    // Metadata cards are synthesized from the OPDB-keyed Machine
                    // record, not scraped — so they carry no Timeline.LastDownloadedAt.
                    // Use the machine's LastSeenAt (refreshed on each OPDB sync) as
                    // the freshness signal so the citation freshness badge shows when
                    // the catalog data was last refreshed instead of "freshness
                    // unknown". Existing cards stay null until the next
                    // --sync-metadata-cards run re-indexes them.
                    LastScrapedUtc: MetadataCardSynthesizer.CardFreshness(machine));

                try
                {
                    var result = await indexer.UpsertAsync(chunkRequest, [chunk], indexerOptions, cancellationToken);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                            Console.Error.WriteLine($"  AI Search rejected chunk '{failure.ChunkId}' for {machine.Title} ({machine.Id}): HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                        failed++;
                    }
                    else
                    {
                        upserted++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Failed to index metadata card for {machine.Title} ({machine.Id}): {ex.Message}");
                    failed++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--sync-metadata-cards complete: upserted={upserted} failed={failed}");
        if (failed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --link-documents / --relink-all (document-to-machine linking pass;
    // processes all pending, failed, and not_in_catalog records in
    // scraped_documents_raw via the 5-tier algorithm and fans resolved documents
    // into scraped_documents). --relink-all first resets prior Linked/NotInCatalog
    // records to Pending so they re-resolve. Gated on IDocumentLinker (Cosmos).
    // Handle --download-documents (fetch not-yet-downloaded raw documents so the
    // linker's page-text tiers can read page-1 content). Runs before linking.
    // Gated on DocumentDownloadService (Cosmos).
    if (downloadDocuments)
    {
        await DownloadDocumentsCommand.RunAsync(host.Services, cancellationToken, forceRedownload);
        return;
    }

    // Handle --download-and-link (combined nightly verb: download to blob, then link).
    // Stage 1 (download) runs first; if it sets a non-zero exit code, Stage 2 (link)
    // is skipped so a missing-Cosmos error is not shadowed by a downstream link failure.
    if (downloadAndLink)
    {
        await DownloadAndLinkCommand.RunAsync(host.Services, cancellationToken, forceRedownload);
        return;
    }

    // Handle --migrate-download-paths (one-shot byte-safe correction of legacy
    // already-rooted file.local_path values; --dry-run reports without changing).
    // Gated on DownloadPathMigrationService (Cosmos).
    if (migrateDownloadPaths)
    {
        await MigrateDownloadPathsCommand.RunAsync(host.Services, dryRun, cancellationToken);
        return;
    }

    if (linkDocuments || relinkAll)
    {
        await LinkDocumentsCommand.RunAsync(host.Services, cancellationToken, relinkAll);
        return;
    }

    // Handle --ensure-azure-foundry (post-deploy Foundry smoke-test, ADR-0014).
    // Resolves IAzureFoundrySmokeProbe from DI; the probe is only registered
    // when AddAzureFoundryIntegration was wired (i.e., AiFoundry:ProjectEndpoint
    // is set). Mirrors the --ensure-cosmos-containers exit-code-2 remediation
    // pattern.
    if (ensureAzureFoundry)
    {
        var probe = host.Services.GetService<IAzureFoundrySmokeProbe>();
        if (probe is null)
        {
            Console.Error.WriteLine(
                "--ensure-azure-foundry requires AI Foundry to be configured. Set " +
                $"{AiFoundryOptions.ProjectEndpointKey} (the deployed Foundry project endpoint URL, e.g. " +
                "https://<account>.services.ai.azure.com/api/projects/<project>).");
            Environment.ExitCode = 2;
            return;
        }

        var result = await probe.ProbeAsync(cancellationToken);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Azure Foundry smoke probe failed: {result.Error}");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(
            $"Azure Foundry deployments verified: chat + embedding deployments present at {result.FoundProjectEndpoint}.");
        return;
    }

    // Handle --ensure-ai-search (post-deploy AI Search smoke-test, ADR-0021,
    // Phase 4 W1-4). Resolves IAzureAiSearchSmokeProbe from DI; the probe
    // is only registered when AddAzureAiSearchIntegration was wired (i.e.,
    // AiSearch:Endpoint is set). Mirrors the --ensure-azure-foundry
    // exit-code-2 remediation pattern.
    if (ensureAiSearch)
    {
        var probe = host.Services.GetService<IAzureAiSearchSmokeProbe>();
        if (probe is null)
        {
            Console.Error.WriteLine(
                "--ensure-ai-search requires Azure AI Search to be configured. Set " +
                $"{AiSearchOptions.EndpointKey} (the deployed search service endpoint URL, e.g. " +
                "https://pinwiz-search-dev-XXXX.search.windows.net).");
            Environment.ExitCode = 2;
            return;
        }

        var result = await probe.ProbeAsync(cancellationToken);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Azure AI Search smoke probe failed: {result.Error}");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(
            $"Azure AI Search verified: endpoint reachable at {result.FoundEndpoint} " +
            $"(expected index: {result.ExpectedIndexName}; index creation lands in Wave 2 W2-3).");
        return;
    }

    // Handle --ensure-rag-index (post-deploy AI Search RAG-index ensure-create,
    // ADR-0021, Phase 4 W2-3). Resolves RagIndexBootstrapper from DI; the
    // bootstrapper is only registered when AddAzureAiSearchIntegration was
    // wired (i.e., AiSearch:Endpoint is set). Mirrors the
    // --ensure-cosmos-containers / --ensure-ai-search exit-code-2 remediation
    // pattern.
    if (ensureRagIndex)
    {
        var bootstrapper = host.Services.GetService<RagIndexBootstrapper>();
        if (bootstrapper is null)
        {
            Console.Error.WriteLine(
                "--ensure-rag-index requires Azure AI Search to be configured. Set " +
                $"{AiSearchOptions.EndpointKey} (the deployed search service endpoint URL, e.g. " +
                "https://pinwiz-search-dev-XXXX.search.windows.net).");
            Environment.ExitCode = 2;
            return;
        }

        var bootstrapResult = await bootstrapper.EnsureCreatedAsync(cancellationToken);
        Console.WriteLine(bootstrapResult.Created
            ? $"AI Search RAG index created: {bootstrapResult.IndexName}"
            : $"AI Search RAG index already present: {bootstrapResult.IndexName}");
        return;
    }

    // Handle --rebuild-rag-index (DESTRUCTIVE drop + recreate). The only delete
    // in the system, and an explicit operator step — the index is a rebuildable
    // projection, so corrections happen by wipe-and-re-ingest, never by per-chunk
    // deletion. After this, re-ingest with --run-rag-backfill + --sync-metadata-cards.
    if (rebuildRagIndex)
    {
        var bootstrapper = host.Services.GetService<RagIndexBootstrapper>();
        if (bootstrapper is null)
        {
            Console.Error.WriteLine(
                "--rebuild-rag-index requires Azure AI Search to be configured. Set " +
                $"{AiSearchOptions.EndpointKey} (the deployed search service endpoint URL).");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("Rebuilding AI Search RAG index (DROP + recreate) — all chunks will be removed...");
        var rebuildResult = await bootstrapper.RecreateAsync(cancellationToken);
        Console.WriteLine(
            $"AI Search RAG index rebuilt: {rebuildResult.IndexName}. " +
            "Re-ingest now: --run-rag-backfill then --sync-metadata-cards.");
        return;
    }

    // Handle --eval (Phase 3 evaluation harness; ADR-0016). Resolves
    // IEvaluationHarness from DI; the harness is only registered when
    // AddAzureFoundryIntegration was wired (i.e., AiFoundry:ProjectEndpoint
    // is set). Mirrors the --ensure-azure-foundry exit-code-2 remediation
    // pattern; on success, prints the results path + aggregate metrics
    // line so a CI run can grep them out without re-reading the JSON.
    if (eval)
    {
        var harness = host.Services.GetService<IEvaluationHarness>();
        if (harness is null)
        {
            Console.Error.WriteLine(
                "--eval requires AI Foundry to be configured. Set " +
                $"{AiFoundryOptions.ProjectEndpointKey} (the deployed Foundry project endpoint URL).");
            Environment.ExitCode = 2;
            return;
        }

        var runResult = await harness.RunAsync(cancellationToken);
        // Nullable means are "n/a" when no row exercised the metric
        // (metric-hygiene fix: gap/refusal rows carry null scores and
        // are excluded from the denominator).
        static string FormatMean(double? mean) =>
            mean is { } value ? value.ToString("F3", CultureInfo.InvariantCulture) : "n/a";
        Console.WriteLine();
        Console.WriteLine($"Evaluation harness completed: {runResult.Aggregate.QuestionCount} questions " +
                          $"({runResult.Aggregate.ErrorCount} errors), " +
                          $"results at {runResult.ResultsPath}");
        Console.WriteLine($"  citation_precision={FormatMean(runResult.Aggregate.CitationPrecisionMean)} " +
                          $"citation_recall={FormatMean(runResult.Aggregate.CitationRecallMean)} " +
                          $"citation_coverage={FormatMean(runResult.Aggregate.CitationCoverageMean)} " +
                          $"subagent_accuracy={FormatMean(runResult.Aggregate.SubagentAccuracyMean)} " +
                          $"refusal_correctness={FormatMean(runResult.Aggregate.RefusalCorrectnessMean)}");
        return;
    }

    // Handle --ask (Phase 3 thin Wizard slice; ADR-0014). Resolves
    // IAiRouter from DI; the router is only registered when
    // AddAzureFoundryIntegration was wired (i.e., AiFoundry:ProjectEndpoint
    // is set). PR 4 ships the skeleton; PR 5/6 add sub-agent grounding +
    // confidence-driven refusal.
    if (!string.IsNullOrWhiteSpace(ask))
    {
        var router = host.Services.GetService<IAiRouter>();
        if (router is null)
        {
            Console.Error.WriteLine(
                "--ask requires AI Foundry to be configured. Set " +
                $"{AiFoundryOptions.ProjectEndpointKey} (the deployed Foundry project endpoint URL).");
            Environment.ExitCode = 2;
            return;
        }

        var answer = await router.AnswerAsync(ask, cancellationToken);
        var json = System.Text.Json.JsonSerializer.Serialize(
            answer,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return;
    }

    // Handle --source opdb (sync OPDB → Cosmos). Special-cased rather than
    // adapted into ISourceScraper because OPDB doesn't yield ScrapedItems —
    // it writes directly to IMachineRepository.
    if (string.Equals(source, "opdb", StringComparison.OrdinalIgnoreCase))
    {
        var sync = host.Services.GetService<IOpdbSyncService>();
        if (sync is null)
        {
            Console.Error.WriteLine(
                "OPDB sync requires Cosmos and OPDB configuration. Set ConnectionStrings:cosmos " +
                "(or Cosmos:AccountEndpoint) AND Opdb:BaseUrl in appsettings.json, or run under Aspire.");
            Environment.ExitCode = 2;
            return;
        }

        var mode = dryRun ? OpdbSyncMode.DryRun : OpdbSyncMode.Apply;
        var result = await sync.SyncAsync(mode, cancellationToken);
        Console.WriteLine();
        if (dryRun)
        {
            Console.WriteLine($"OPDB sync (DRY RUN — no writes): fetched {result.Fetched}, " +
                              $"would-insert {result.Inserted}, would-update {result.Updated}, " +
                              $"skipped {result.Skipped}, aliases-as-editions {result.AliasesAppended} " +
                              $"(orphaned {result.AliasesOrphaned}), duration {result.Duration.TotalSeconds:N1}s");
        }
        else
        {
            Console.WriteLine($"OPDB sync: fetched {result.Fetched}, inserted {result.Inserted}, " +
                              $"updated {result.Updated}, skipped {result.Skipped}, " +
                              $"aliases-as-editions {result.AliasesAppended} (orphaned {result.AliasesOrphaned}), " +
                              $"duration {result.Duration.TotalSeconds:N1}s");
        }
        return;
    }

    // Handle --rebuild-catalog-stats (rebuildable-projection backstop for the
    // catalog_stats Tier-3 read model; ADR-0031/ADR-0036). Resolves
    // ICatalogStatsRebuildService from DI; the service is only registered when
    // AddCatalogStatsRebuild is wired inside the cosmosWired gate below.
    // Mirrors the --ensure-cosmos-containers exit-code-2 remediation pattern.
    if (rebuildCatalogStats)
    {
        var rebuilder = host.Services.GetService<ICatalogStatsRebuildService>();
        if (rebuilder is null)
        {
            Console.Error.WriteLine(
                "--rebuild-catalog-stats requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        var (manufacturers, machines) = await rebuilder.RebuildAsync(cancellationToken);
        Console.WriteLine($"Rebuilt catalog_stats: {manufacturers} manufacturers, {machines} machines.");
        return;
    }

    // Default behavior: scrape (discover + upsert to Cosmos).
    var scrapeResult = await orchestrator.ScrapeAsync(source, dryRun, cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Discovery: {scrapeResult.TotalLinks} links");

    if (!scrapeResult.Errors.IsEmpty)
    {
        Console.WriteLine($"  {scrapeResult.Errors.Count} errors during discovery");
    }
});

return await rootCommand.Parse(args).InvokeAsync();

// ── Host Builder ──────────────────────────────────────────────────────────────

static IHost CreateHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Aspire shared defaults — OpenTelemetry (logs / metrics / traces with the
    // OTLP exporter the AppHost dashboard injects via OTEL_EXPORTER_OTLP_ENDPOINT),
    // service discovery, standard HTTP resilience, and health checks. When the CLI
    // is launched standalone (no AppHost), these registrations are still safe — the
    // OTLP exporter only activates when the env var is present.
    builder.AddServiceDefaults();

    // Configuration
    builder.Services.Configure<ScraperSettings>(
        builder.Configuration.GetSection(ScraperSettings.SectionName));

    // Override data path from environment variable (for Docker)
    var dataPath = Environment.GetEnvironmentVariable("DATA_PATH");
    if (!string.IsNullOrEmpty(dataPath))
    {
        builder.Services.PostConfigure<ScraperSettings>(s => s.DataPath = dataPath);
    }

    // Logging
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(
        args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);

    // Polite-scraping foundation (per-origin throttle + robots.txt cache + 429 backoff,
    // ADR-aligned User-Agent identifying the project). MUST be registered before the
    // HttpClient and scraper registrations below — the polite User-Agent is what the
    // typed clients pull as their default UA.
    builder.Services.AddPoliteScraping(builder.Configuration);

    // Cosmos persistence — gated. When Aspire (or appsettings) provides a Cosmos
    // connection, register the persistence layer + OPDB integration + the
    // Cosmos-backed politeness-overrides resolver (which replaces the default
    // resolver registered by AddPoliteScraping). When neither is present, the CLI
    // runs as a pure scraper without Cosmos, OPDB, or per-source overrides — the
    // behavior shipped through Phase 1.
    //
    // SECURITY NOTE: gating is by *presence* of the config key, NOT validation
    // of the endpoint. An attacker who can already set env vars on this CLI
    // process can already run arbitrary code; redirecting Cosmos reads is
    // strictly weaker than RCE and is accepted in the project's threat model.
    // The Cosmos:AccountEndpoint value comes from Bicep outputs in production
    // (Managed-Identity path, no shared secret); the connection string for
    // Aspire-managed local dev points at the loopback emulator.
    //
    // The shared host gate (CosmosHostRegistration) registers the CosmosClient
    // (emulator or Managed-Identity) + AddCosmosPersistence and returns whether
    // Cosmos was wired; the CLI gates its own extras (politeness overrides,
    // seeders, catalog-stats rebuild, OPDB sync) on that signal.
    var cosmosWired = builder.AddHostCosmosPersistence();
    if (cosmosWired)
    {
        builder.Services.AddCosmosBackedPolitenessOverrides();

        // Document blob store (pinwiz-raw container) — registered here so
        // --download-documents can write to durable blob storage (blob name =
        // the same relative path that DocumentDownloadService builds). The RAG
        // ingestion path also calls AddDocumentBlobStore inside
        // AddBlobDocumentBytesSource; AddSingleton is idempotent (first wins),
        // so double-registration is harmless. Gracefully no-ops when neither
        // ConnectionStrings:blobs (Aspire/Azurite) nor Storage:BlobEndpoint
        // (deployed managed identity) is present — missing config produces a
        // loud DI resolution error at the point of first use.
        builder.Services.AddDocumentBlobStore(builder.Configuration);

        // Ingestion-sources seeder. Application-layer service depending on
        // IIngestionSourceRepository (registered by AddCosmosPersistence above);
        // gated alongside Cosmos because there's nothing for it to write to
        // without the repository.
        builder.Services.AddTransient<IIngestionSourceSeeder, IngestionSourceSeeder>();

        // Featured-machine seed loader. Application-layer service depending on
        // IFeaturedMachineRepository (registered by AddCosmosPersistence above);
        // gated alongside Cosmos because --seed-featured-machines has no target
        // container without the repository. IFeaturedMachineSeedLoader is the
        // file-system read; IFeaturedMachineRepository is the Cosmos write target.
        // Both are checked via GetService in the --seed-featured-machines handler.
        builder.Services.AddSingleton<IFeaturedMachineSeedLoader, FeaturedMachineSeedLoader>();

        // catalog_stats rebuild service (--rebuild-catalog-stats). Depends on
        // IMachineRepository + two CosmosRepository<T> wrappers; all three are
        // available inside this cosmosWired gate. GetService<ICatalogStatsRebuildService>()
        // in the handler returns null (→ exit code 2) when Cosmos is not configured.
        builder.Services.AddCatalogStatsRebuild();
    }

    // OPDB integration — gated on Opdb:BaseUrl. Sync writes to IMachineRepository,
    // which only exists when AddCosmosPersistence is wired; treat missing Cosmos
    // wiring as missing-OPDB-wiring too (the --source opdb dispatch will print a
    // clear error in that case).
    var opdbWired = cosmosWired
        && !string.IsNullOrWhiteSpace(builder.Configuration[OpdbOptions.BaseUrlKey]);
    if (opdbWired)
    {
        builder.Services.AddOpdbIntegration(builder.Configuration);
    }

    // Pinball Map integration — gated on PinballMap:BaseUrl. Phase 3 Wave 1
    // ships the read-side client (region locations on demand). Unlike OPDB
    // there is no batch sync that writes to a repository, so the wiring is
    // independent of Cosmos — a downstream consumer (Wizard answer flow,
    // future location-aware features) injects the client directly.
    var pinballMapWired = !string.IsNullOrWhiteSpace(builder.Configuration[PinballMapOptions.BaseUrlKey]);
    if (pinballMapWired)
    {
        builder.Services.AddPinballMapIntegration(builder.Configuration);
    }

    // Azure AI Foundry integration — gated on AiFoundry:ProjectEndpoint
    // (ADR-0014). Phase 3 PR 2a ships the smoke probe only; Wave 2 PR 4
    // adds IFoundryAgentFactory + IAiRouter on top. Treat absence as a
    // valid configuration in Phase 0/1/2 (no AI surface needed) and in
    // Aspire-emulator local dev (no Foundry to connect to).
    var foundryWired = !string.IsNullOrWhiteSpace(builder.Configuration[AiFoundryOptions.ProjectEndpointKey]);
    if (foundryWired)
    {
        builder.Services.AddAzureFoundryIntegration(builder.Configuration);
    }

    // Azure AI Search integration — gated on AiSearch:Endpoint (ADR-0021,
    // Phase 4 W1-4). Phase 4 ships the smoke probe at this gate; Wave 2
    // W2-3 extends consumption for index creation + document upsert,
    // Wave 3 W3-3 adds hybrid retrieval. Treat absence as a valid
    // configuration in Phase 0/1/2/3 (no RAG retrieval surface) and in
    // local dev before the H1 hand-off (deployAiSearch may still be false
    // in main-shared.dev.local.bicepparam to defer Basic-SKU cost).
    var aiSearchWired = !string.IsNullOrWhiteSpace(builder.Configuration[AiSearchOptions.EndpointKey]);
    if (aiSearchWired)
    {
        builder.Services.AddAzureAiSearchIntegration(builder.Configuration);
    }

    // RAG backfill service — gated on all three backend services being present.
    // Registers the full ingestion stack (pipeline + chunker + extractor +
    // Cosmos-backed IIndexState + backfill service) so `--run-rag-backfill`
    // can populate the AI Search index from existing scraped_documents without
    // running the Change Feed Processor.
    if (cosmosWired && aiSearchWired && foundryWired)
    {
        builder.Services.AddRagIngestionPipeline();
        builder.Services.AddHybridChunker();
        builder.Services.AddPdfDocumentTextExtractor(builder.Configuration);
        builder.Services.AddRagBackfillService(builder.Configuration);
        builder.Services.AddMetadataCardSynthesizer();
    }

    var politenessOptions = builder.Configuration.GetSection(PolitenessOptions.SectionName)
        .Get<PolitenessOptions>() ?? new PolitenessOptions();
    var politeUserAgent = politenessOptions.UserAgent;

    // HTTP clients with shared resilience pipeline (Microsoft.Extensions.Http.Resilience).
    // The resilience handler applies at the HttpMessageHandler layer; the politeness gate
    // (extended via PoliteScraperBase) applies above it at request time. Both layers
    // serve different purposes:
    //   - Resilience pipeline: transient retries (5xx, network errors), concurrency limit
    //   - Politeness gate: per-origin throttle, robots.txt, 429 abort
    var httpSettings = builder.Configuration.GetSection(ScraperSettings.SectionName)
        .Get<ScraperSettings>() ?? new ScraperSettings();

    builder.Services.AddHttpClient<ManualsScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(politeUserAgent);
        client.Timeout = TimeSpan.FromSeconds(120);
    })
    .AddResilienceHandler("stern-html", pipeline => ConfigureSternPipeline(pipeline, httpSettings));

    builder.Services.AddHttpClient<FileDownloader>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(politeUserAgent);
        client.Timeout = TimeSpan.FromSeconds(300);
    })
    .AddResilienceHandler("stern-download", pipeline => ConfigureSternPipeline(pipeline, httpSettings));

    // Bind the IFileDownloader contract to the FileDownloader implementation
    // already constructed by the typed-client registration above.
    builder.Services.AddTransient<IFileDownloader>(sp => sp.GetRequiredService<FileDownloader>());

    // Infrastructure
    builder.Services.AddSingleton<PlaywrightFactory>();

    // Scrapers — all extend PoliteScraperBase or PolitePlaywrightScraperBase
    // and route every request through the politeness gate.
    builder.Services.AddTransient<GameListingScraper>();
    builder.Services.AddTransient<ISourceScraper, ManualsScraper>();
    builder.Services.AddTransient<ISourceScraper, GamePageScraper>();
    builder.Services.AddTransient<ISourceScraper, ServiceBulletinScraper>();

    // JJP scraper (Phase 1.2 — Shopify/HTTP, sitemap-first discovery).
    builder.Services.AddJjpScraping(builder.Configuration);

    // American Pinball scraper (Phase 1.2 — custom-CMS/HTTP, sitemap-first discovery,
    // DOM-heuristic title extraction, downloadable PDF/ZIP/SPK link extraction).
    builder.Services.AddAmericanPinballScraping(builder.Configuration);

    // Spooky Pinball scraper (Phase 1.2 — WordPress + WooCommerce + Yoast,
    // discovers games via the WP REST API and identifies them by single-S3-slug
    // firmware-link signature in page content).
    builder.Services.AddSpookyPinballScraping(builder.Configuration);

    // Pinball Brothers scraper (Phase 1.3 — WordPress + Visual Composer,
    // discovers games via the WP REST API and identifies them by the
    // `-pinball` slug suffix on top-level pages).
    builder.Services.AddPinballBrothersScraping(builder.Configuration);

    // Barrels of Fun scraper (Phase 1.3 — WooCommerce on shop.kollectfun.com,
    // discovers machines via the /product-category/machines/ category page
    // and extracts JSON-LD product schema from each product page).
    builder.Services.AddBarrelsOfFunScraping(builder.Configuration);

    // Chicago Gaming Company scraper (Phase 1.3 — custom Nginx-served HTML,
    // discovers machines via the /coinop/ index page, extracts title from
    // page <title> with manufacturer suffix stripped, plus same-host PDFs).
    builder.Services.AddChicagoGamingScraping(builder.Configuration);

    // Multimorphic scraper (Phase 1.3 — WordPress + WooCommerce, sitemap-first
    // discovery filtered to /store/p3-game-kits/multimorphic-game-kits/, JSON-LD
    // product schema; deliberately excludes 3rd-party kits which belong to
    // their respective studios per OPDB attribution).
    builder.Services.AddMultimorphicScraping(builder.Configuration);

    // TimeProvider is required by ScraperReconciliationService. Registered here so
    // the reconciler works in Phase 1/2 environments where the RAG pipeline (which
    // also registers TimeProvider.System) is not wired.
    builder.Services.TryAddSingleton(TimeProvider.System);
    builder.Services.AddTransient<IScraperReconciliationService, ScraperReconciliationService>();

    // Orchestrator — DI resolves all constructor parameters automatically.
    // IRawDocumentRepository is registered by AddCosmosPersistence; the CLI
    // requires Cosmos to be configured for the scraping path.
    builder.Services.AddTransient<ScraperOrchestrator>();

    // Ensure data directories exist
    var settings = builder.Configuration.GetSection(ScraperSettings.SectionName)
        .Get<ScraperSettings>() ?? new ScraperSettings();
    if (!string.IsNullOrEmpty(dataPath)) settings.DataPath = dataPath;

    Directory.CreateDirectory(settings.DownloadsPath);
    Directory.CreateDirectory(settings.MetadataPath);
    Directory.CreateDirectory(settings.LogsPath);
    Directory.CreateDirectory(settings.SnapshotsPath);
    Directory.CreateDirectory(settings.HistoryPath);

    return builder.Build();
}

// ── Resilience pipeline ───────────────────────────────────────────────────────

// Two-strategy pipeline: concurrency limiter (politeness) + retry (transient failures).
// Per-attempt timeout is intentionally NOT added — the per-client HttpClient.Timeout
// already applies per attempt and accommodates large PDF downloads. See
// docs/http-resilience-research.md for the full rationale.
static void ConfigureSternPipeline(
    ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
    ScraperSettings settings)
{
    pipeline.AddConcurrencyLimiter(permitLimit: Math.Max(1, settings.MaxConcurrentDownloads));

    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = Math.Max(0, settings.MaxRetries),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromMilliseconds(Math.Max(1, settings.InitialRetryDelayMs)),
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldRetryAfterHeader = true,
        // Default ShouldHandle covers HTTP 5xx, 408, 429, HttpRequestException,
        // and TimeoutRejectedException — exactly the set we want.
    });
}
