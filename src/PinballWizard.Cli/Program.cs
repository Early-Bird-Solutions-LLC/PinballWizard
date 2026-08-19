using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PinballWizard.Cli;
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
using PinballWizard.Application.Findability;
using PinballWizard.Application.Landing;
using PinballWizard.Application.SeedData;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Credentials;
using PinballWizard.Infrastructure.Documents;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Integrations.SilverballLabs;
using PinballWizard.Infrastructure.Integrations.PinballMap;
using PinballWizard.Infrastructure.Catalog;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Coverage;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Application.Rag.MetadataCards;
using PinballWizard.Application.Rag.GameOverviews;
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
using PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Spooky;
using PinballWizard.Infrastructure.Scraping.Stern;
using PinballWizard.Infrastructure.Scraping.Kineticist;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using PinballWizard.Infrastructure.Scraping.Twip;
using PinballWizard.ServiceDefaults;
using Polly;
using Polly.Retry;

// ── CLI Definition ────────────────────────────────────────────────────────────

var sourceOption = new Option<string?>("--source", "-s")
{
    Description = "Which source(s) to scrape: manuals, games, bulletins, jjp, jjp_support, ap, spooky, pinballbrothers, barrelsoffun, cgc, multimorphic, opdb, all. " +
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

var playwrightWithDepsOption = new Option<bool>("--with-deps")
{
    Description = "With --install-playwright, also install Chromium's OS library dependencies. Requires root; intended for container image builds, where downloading the browser alone leaves it unable to launch."
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

var gcRagIndexOption = new Option<bool>("--gc-rag-index")
{
    Description = "Reconcile the RAG index against the scraped_documents catalog: delete index chunks whose (document_id, machine_id) pair has no backing fan-out row. This is the delete-propagation mechanism the Cosmos Change Feed (latest-version mode) cannot provide — after a --relink-all prunes stale fan-out rows, their index chunks linger as orphans until this pass removes them. Unlike --rebuild-rag-index (full wipe + re-ingest), this is a surgical, idempotent, read-mostly reconcile. Combine with --dry-run to preview orphan pairs without deleting. Requires Cosmos and Azure AI Search to be configured."
};

var backfillSynthesizedRawDocsOption = new Option<bool>("--backfill-synthesized-raw-docs")
{
    Description = "Heal dead citations to synthesized sources. Scans the RAG index for synthesized documents (Kineticist tutorials, Tilt Forums rulesheets, TWIP newsletters, Pinball Brothers Freshdesk articles) and writes a scraped_documents_raw row for any that lack one, reconstructing title / source url / type / manufacturer / freshness from the indexed metadata. Such docs are cited in Wizard answers but resolve to \"Document not found\" at /documents/{id} until backfilled — the case the live sync verbs cannot cover for docs they no longer re-index (e.g. a game slug that no longer resolves to a machine). Idempotent and non-destructive: never overwrites a raw doc the live sync already wrote. Combine with --dry-run to preview. Requires Cosmos and Azure AI Search to be configured."
};

var ensureMachineIndexOption = new Option<bool>("--ensure-machine-index")
{
    Description = "Non-destructive: ensures the AI Search machine findability index schema and synonym map exist (creates them if absent; no-op if already present). Safe to call at startup or in CI without data loss. Use --rebuild-machine-index to wipe and re-project. Requires AiSearch:Endpoint and Cosmos to be configured. Exit code 2 + remediation hint when not configured."
};

var rebuildMachineIndexOption = new Option<bool>("--rebuild-machine-index")
{
    Description = "DESTRUCTIVE: drops and recreates the AI Search machine findability index (ADR-0049 phase 2a), then projects all Machine records from Cosmos into it. Wipes ALL existing indexed documents — run only when a schema-breaking change requires a full rebuild. For non-destructive schema + synonym-map updates use --ensure-machine-index. Requires AiSearch:Endpoint and Cosmos to be configured. Exit code 2 + remediation hint when not configured."
};

var askOption = new Option<string?>("--ask")
{
    Description = "Phase 3 thin Wizard slice: invokes the IAiRouter end-to-end against the deployed Foundry project (per ADR-0014) for a single question and prints the WizardAnswer JSON. Requires AiFoundry:ProjectEndpoint to be configured. Wave 2 PR 4 ships the skeleton (Wizard agent only); PR 5 adds sub-agents + getMachineByTitle grounding; PR 6 adds confidence-driven refusal."
};

var evalOption = new Option<bool>("--eval")
{
    Description = "Phase 3 evaluation harness (ADR-0016): drives every question in data/eval/wizard.v1.jsonl through IAiRouter, scores responses with the four custom code-based evaluators (citation precision/recall, subagent accuracy, refusal correctness), and writes a timestamped JSON file to data/eval/results/. Idempotently registers the evaluator definitions with the Foundry project so they are surfaced in the portal alongside built-ins. Requires AiFoundry:ProjectEndpoint to be configured."
};

var corpusCoverageOption = new Option<bool>("--corpus-coverage")
{
    Description = "Corpus coverage probe: for each (source × document_type) cell with indexed content, assert presence + retrievability (a query auto-derived from a sample chunk retrieves content from that cell). Writes data/eval/results/coverage.{ts}.json and exits non-zero on gaps. Requires AiSearch:Endpoint. No LLM calls."
};

var probeRetrievalOption = new Option<string?>("--probe-retrieval")
{
    Description = "Classify a candidate eval JSONL by first-stage retrieval rank (Phase 4.5 H5b prep). " +
                  "Reads each EvalQuestion from <input.jsonl>, calls IRetrievalRankProbe.ProbeAsync, " +
                  "and writes <input>.classified.jsonl with 'slice' and 'first_stage_rank' populated. " +
                  "Prints a one-line slice-distribution summary (easy=N reranker-sensitive=N retrieval-miss=N). " +
                  "IMPORTANT: this verb measures FIRST-STAGE rank (before Cohere cross-encoder reranking). " +
                  "Requires Rag:CrossEncoder:Enabled=false — the command exits with code 2 and a clear " +
                  "error message if the reranker is on, because the measurement would be corrupted. " +
                  "Requires AiSearch:Endpoint to be configured. Exit code 2 + remediation hint when not configured."
};

var runRagBackfillOption = new Option<bool>("--run-rag-backfill")
{
    Description = "One-shot RAG index backfill: iterates all scraped_documents via the raw Change Feed stream iterator (no lease checkpoints) and runs each document through the full RAG ingestion pipeline (extract → chunk → embed → AI Search upsert). Idempotent: documents already indexed with the same content hash are skipped. Use after provisioning a fresh RAG index to populate it from existing scraped documents before the Change Feed Processor starts handling ongoing writes. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured."
};

var syncMetadataCardsOption = new Option<bool>("--sync-metadata-cards")
{
    Description = "Synthesize metadata_card chunks from the Cosmos machines container and upsert them into AI Search. One card per machine record — covers title, manufacturer, year, designers, themes, editions, and MSRP. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured."
};

var syncGameOverviewsOption = new Option<bool>("--sync-game-overviews")
{
    Description = "Synthesize and index GameOverview documents from each Machine's scraped game-page OverviewProse + per-edition content. Mirrors --sync-metadata-cards. No-op for machines without overview content. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
};

var syncKineticistTutorialsOption = new Option<bool>("--sync-kineticist-tutorials")
{
    Description = "Fetch and index all Kineticist pinball tutorial articles as Rulesheet documents in AI Search (ADR-0043 / Domain-2). Each article is fetched as clean Markdown via the .md URL suffix — no PDF extraction. Machine linking uses IMachineTitleLookupRepository; unresolvable slugs are logged and skipped. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
};

var syncTiltForumsRulesheetsOption = new Option<bool>("--sync-tiltforums-rulesheets")
{
    Description = "Fetch and index Tilt Forums community rulesheets as Rulesheet documents in AI Search (ADR-0050 / Domain-2). Discovers rulesheets from the manufacturer-grouped master list wiki page, resolves each to a catalog machine scoped to its manufacturer (never guessing on cross-manufacturer title collisions), and indexes the wiki post content. Idempotent: safe to re-run. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
};

var syncTwipNewsletterOption = new Option<bool>("--sync-twip-newsletter")
{
    Description = "Fetch and index recent TWIP (This Week in Pinball) newsletter issues as NewsDigest documents in AI Search. Discovers articles from twip.kineticist.com/sitemap.xml, extracts content via AngleSharp (JSON-LD + body), and synthesizes chunks for indexing. Idempotent: safe to re-run. Use --twip-since to control the lookback window. Requires Cosmos, AI Search, and AI Foundry to be configured. ADR-0043.",
};

var twipSinceOption = new Option<string?>("--twip-since")
{
    Description = "ISO-8601 date (e.g. 2026-06-01). Limits --sync-twip-newsletter to articles published on or after this date. Defaults to Twip:DefaultLookbackDays (14) days ago. Accepts date portion only.",
};

var syncPbFreshdeskArticlesOption = new Option<bool>("--sync-pb-freshdesk-articles")
{
    Description = "Fetch and index text-only Pinball Brothers Freshdesk support articles (troubleshooting Q&A, \"how to\" guides, update notes with no PDF attachment) as SupportArticle documents in AI Search. Attachment-bearing articles (Manuals, Rulebooks, Schematics, Service Bulletins) are handled separately by --source pb_freshdesk, not this verb. Machine linking uses IMachineTitleLookupRepository keyed on the Freshdesk category name (Alien/Queen/ABBA/Predator); General-category articles index under a synthetic 'pb_support' machine id. Idempotent: safe to re-run.",
};

var syncP3SdkDocsOption = new Option<bool>("--sync-p3-sdk-docs")
{
    Description = "Index Multimorphic P3 SDK developer documents (per-module UsageInstructions + INSTALL.txt + ReleaseNotes.txt) as SdkGuide chunks in AI Search. Reads from the local SDK zip or an already-extracted directory specified by --sdk-path. Skips the 1,032 Doxygen HTML files (low narrative RAG value). Idempotent: document_id is a stable hash of the file path. Requires Azure AI Search and Azure AI Foundry to be configured.",
};

var sdkPathOption = new Option<string?>("--sdk-path")
{
    Description = "Path to the P3 SDK zip (P3_SDK_V0.9.zip) or an extracted directory. Defaults to C:\\earlybird\\PinballWizard\\P3_SDK_V0.9.zip. Used with --sync-p3-sdk-docs.",
};

var refreshGameOverviewsOption = new Option<bool>("--refresh-game-overviews")
{
    Description = "Atomic Stern game-page refresh: scrape the game-page source, reconcile onto Machine records, then synthesize and index GameOverview docs. Equivalent to --source games followed by --sync-game-overviews, in one polite pass. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
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
    Description = "[DEPRECATED — ADR-0039] No-op against any post-ADR-0039 corpus: the downloader now writes documents to the pinwiz-raw blob container instead of local disk, so there are no on-disk paths left to migrate. Slated for removal in a follow-up PR. (Historically: a one-shot, byte-safe migration that corrected legacy already-rooted scraped_documents_raw file.local_path values — e.g. 'data/downloads/manualspage/x.pdf' — to the clean relative form 'manualspage/x.pdf', verifying each file's SHA-256 before moving it.) Requires Cosmos to be configured."
};

var rebuildCatalogStatsOption = new Option<bool>("--rebuild-catalog-stats")
{
    Description = "Recomputes every per-manufacturer catalog_stats rollup from scratch: streams all machines, reads each machine's scraped_documents (single-partition), aggregates doc counts and type distribution, and upserts the authoritative per-manufacturer rollup document. This is the rebuildable-projection backstop (ADR-0031/ADR-0036) — also the only path that sets authoritative identity fields (EditionLabel, GroupId, Year, IsOpdbOnly) on each MachineStatEntry from the live Machine record. Idempotent. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var reclassifyDocumentsOption = new Option<bool>("--reclassify-documents")
{
    Description = "Re-run ClassifyDocumentType over every record in scraped_documents_raw using the stored Source fields (LinkText, FileUrl, DiscoveryContext) and write back ONLY the records whose classification changed. Idempotent: a second run is a no-op. Makes no external HTTP calls — operates entirely on already-stored Cosmos data (polite-by-construction). Use after a classification rule change (e.g. PR #507 added Rulesheet) to fix existing Other-typed records without a full re-scrape. After this, run --relink-all to fan updated types into scraped_documents so the RAG ingestion worker picks them up. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var auditCatalogOption = new Option<bool>("--audit-catalog")
{
    Description = "Proactive catalog-quality audit. Streams all machines and reports title-superset collisions — games whose title is BOTH an exact game and a subtitle-prefix of a different OPDB group (e.g. 'Iron Maiden' 1981 vs 'Iron Maiden: Legacy of the Beast' 2018). These are the catalog shape behind the #532 mis-grounding class; surfacing them lets us add eval coverage per collision before a prospect finds the gap. Read-only (no external HTTP, no writes). Exit code 0 = no collisions, 3 = collisions found (so CI/cron can alert). Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var backfillManufacturerSlugsOption = new Option<bool>("--backfill-manufacturer-slugs")
{
    Description = "Backfills Machine.ManufacturerSlugs from /game/{slug}/ cross-reference URLs already captured in scraped_documents_raw, for machines a scraper reconciliation run never reached (issue #672). A --source games run's reconciliation only covers games discoverable in that run — e.g. Stern's currently-marketed lineup — so titles retired from that listing keep an empty ManufacturerSlugs entry forever, even though their documents already carry a valid cross-reference to the game page (captured e.g. when a manual's 'Specs & Manual tab' was scraped). Reuses the same franchise-title matching as scraper reconciliation. No external HTTP calls — operates entirely on already-stored Cosmos data. Idempotent: a slug already present on any machine in the partition is left untouched. Run --relink-all afterward so the linker's Tier 1 (xref_slug) re-resolves documents against the newly-backfilled slugs. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

// READ-ONLY capture verbs that write fixture files for the Wave-2 regression gate (ADR-0054 S3).
// Neither touches any application state in Cosmos — they only stream and write local JSON files.

var captureGoldenSetOption = new Option<bool>("--capture-golden-set")
{
    Description = "READ-ONLY. Streams scraped_documents_raw where link_status in (Linked, ManuallyLinked) and " +
                  "writes tests/PinballWizard.Application.Tests/Fixtures/Linking/golden-link-set.captured.json " +
                  "and a companion CAPTURE.md. The resulting fixture is the Wave-2 regression gate: " +
                  "GoldenLinkSetReplayTests replays every binding and fails on mis-attribution. " +
                  "Operator-gated: run only against the fully re-linked corpus, after --relink-all. " +
                  "Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var captureReconcilerParityOption = new Option<bool>("--capture-reconciler-parity")
{
    Description = "READ-ONLY. Streams the machines container and writes a per-manufacturer ManufacturerSlugs " +
                  "snapshot to tests/PinballWizard.Application.Tests/Fixtures/Sync/reconciler-parity.captured.json " +
                  "and a companion CAPTURE.md. The resulting fixture is used by ReconcilerParityReplayTests to " +
                  "assert that the reconciler algorithm still matches the same slug count as at capture time — " +
                  "a normalization regression shows up as a count drop. " +
                  "Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var capturePageTextOption = new Option<bool>("--capture-page-text")
{
    Description = "Capture page-tier excerpts + bindings for the #832 replay gate (requires Cosmos + blob storage)."
};

var rootCommand = new RootCommand("PinballWizard — Stern Pinball content scraper");
rootCommand.Options.Add(sourceOption);
rootCommand.Options.Add(dryRunOption);
rootCommand.Options.Add(installPlaywrightOption);
rootCommand.Options.Add(playwrightWithDepsOption);
rootCommand.Options.Add(ensureCosmosContainersOption);
rootCommand.Options.Add(seedIngestionSourcesOption);
rootCommand.Options.Add(seedFeaturedMachinesOption);
rootCommand.Options.Add(ensureAzureFoundryOption);
rootCommand.Options.Add(ensureAiSearchOption);
rootCommand.Options.Add(ensureRagIndexOption);
rootCommand.Options.Add(rebuildRagIndexOption);
rootCommand.Options.Add(gcRagIndexOption);
rootCommand.Options.Add(backfillSynthesizedRawDocsOption);
rootCommand.Options.Add(ensureMachineIndexOption);
rootCommand.Options.Add(rebuildMachineIndexOption);
rootCommand.Options.Add(askOption);
rootCommand.Options.Add(evalOption);
rootCommand.Options.Add(corpusCoverageOption);
rootCommand.Options.Add(probeRetrievalOption);
rootCommand.Options.Add(runRagBackfillOption);
rootCommand.Options.Add(syncMetadataCardsOption);
rootCommand.Options.Add(syncGameOverviewsOption);
rootCommand.Options.Add(syncKineticistTutorialsOption);
rootCommand.Options.Add(syncTiltForumsRulesheetsOption);
rootCommand.Options.Add(syncTwipNewsletterOption);
rootCommand.Options.Add(twipSinceOption);
rootCommand.Options.Add(syncPbFreshdeskArticlesOption);
rootCommand.Options.Add(syncP3SdkDocsOption);
rootCommand.Options.Add(sdkPathOption);
rootCommand.Options.Add(refreshGameOverviewsOption);
rootCommand.Options.Add(linkDocumentsOption);
rootCommand.Options.Add(relinkAllOption);
rootCommand.Options.Add(downloadDocumentsOption);
rootCommand.Options.Add(downloadAndLinkOption);
rootCommand.Options.Add(forceRedownloadOption);
rootCommand.Options.Add(migrateDownloadPathsOption);
rootCommand.Options.Add(rebuildCatalogStatsOption);
rootCommand.Options.Add(reclassifyDocumentsOption);
rootCommand.Options.Add(auditCatalogOption);
rootCommand.Options.Add(backfillManufacturerSlugsOption);
rootCommand.Options.Add(captureGoldenSetOption);
rootCommand.Options.Add(captureReconcilerParityOption);
rootCommand.Options.Add(capturePageTextOption);

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var source = parseResult.GetValue(sourceOption);
    var dryRun = parseResult.GetValue(dryRunOption);
    var installPw = parseResult.GetValue(installPlaywrightOption);
    var installPwWithDeps = parseResult.GetValue(playwrightWithDepsOption);
    var ensureCosmos = parseResult.GetValue(ensureCosmosContainersOption);
    var seedIngestionSources = parseResult.GetValue(seedIngestionSourcesOption);
    var seedFeaturedMachines = parseResult.GetValue(seedFeaturedMachinesOption);
    var ensureAzureFoundry = parseResult.GetValue(ensureAzureFoundryOption);
    var ensureAiSearch = parseResult.GetValue(ensureAiSearchOption);
    var ensureRagIndex = parseResult.GetValue(ensureRagIndexOption);
    var rebuildRagIndex = parseResult.GetValue(rebuildRagIndexOption);
    var gcRagIndex = parseResult.GetValue(gcRagIndexOption);
    var backfillSynthesizedRawDocs = parseResult.GetValue(backfillSynthesizedRawDocsOption);
    var ensureMachineIndex = parseResult.GetValue(ensureMachineIndexOption);
    var rebuildMachineIndex = parseResult.GetValue(rebuildMachineIndexOption);
    var ask = parseResult.GetValue(askOption);
    var eval = parseResult.GetValue(evalOption);
    var corpusCoverage = parseResult.GetValue(corpusCoverageOption);
    var probeRetrieval = parseResult.GetValue(probeRetrievalOption);
    var runRagBackfill = parseResult.GetValue(runRagBackfillOption);
    var syncMetadataCards = parseResult.GetValue(syncMetadataCardsOption);
    var syncGameOverviews = parseResult.GetValue(syncGameOverviewsOption);
    var syncKineticistTutorials = parseResult.GetValue(syncKineticistTutorialsOption);
    var syncTiltForumsRulesheets = parseResult.GetValue(syncTiltForumsRulesheetsOption);
    var syncTwipNewsletter = parseResult.GetValue(syncTwipNewsletterOption);
    var twipSince = parseResult.GetValue(twipSinceOption);
    var syncPbFreshdeskArticles = parseResult.GetValue(syncPbFreshdeskArticlesOption);
    var syncP3SdkDocs = parseResult.GetValue(syncP3SdkDocsOption);
    var sdkPath = parseResult.GetValue(sdkPathOption);
    var refreshGameOverviews = parseResult.GetValue(refreshGameOverviewsOption);
    var linkDocuments = parseResult.GetValue(linkDocumentsOption);
    var relinkAll = parseResult.GetValue(relinkAllOption);
    var downloadDocuments = parseResult.GetValue(downloadDocumentsOption);
    var downloadAndLink = parseResult.GetValue(downloadAndLinkOption);
    var forceRedownload = parseResult.GetValue(forceRedownloadOption);
    var migrateDownloadPaths = parseResult.GetValue(migrateDownloadPathsOption);
    var rebuildCatalogStats  = parseResult.GetValue(rebuildCatalogStatsOption);
    var reclassifyDocuments  = parseResult.GetValue(reclassifyDocumentsOption);
    var auditCatalog         = parseResult.GetValue(auditCatalogOption);
    var backfillManufacturerSlugs = parseResult.GetValue(backfillManufacturerSlugsOption);
    var captureGoldenSet = parseResult.GetValue(captureGoldenSetOption);
    var captureReconcilerParity = parseResult.GetValue(captureReconcilerParityOption);
    var capturePageText = parseResult.GetValue(capturePageTextOption);

    // --with-deps only modifies --install-playwright. Accepting it silently on its
    // own would let a mistyped or reordered container build look like it installed
    // OS dependencies when it did nothing at all — the same class of quiet failure
    // this change exists to fix.
    if (installPwWithDeps && !installPw)
    {
        Console.Error.WriteLine("--with-deps has no effect without --install-playwright.");
        Environment.ExitCode = 2;
        return;
    }

    // Handle --install-playwright
    if (installPw)
    {
        Console.WriteLine(installPwWithDeps
            ? "Installing Playwright browsers and OS dependencies..."
            : "Installing Playwright browsers...");
        PlaywrightFactory.InstallBrowsers(installPwWithDeps);
        Console.WriteLine("Playwright browsers installed successfully.");
        return;
    }

    // Build host with DI
    using var host = CreateHost(args);

    // OpenTelemetry providers (TracerProvider / MeterProvider) are registered
    // as lazy DI singletons by AddOpenTelemetry() in ServiceDefaults and are
    // only instantiated when TelemetryHostedService.StartAsync() fires.
    // Without this call the providers are NEVER created, so no ActivityListener
    // subscribes to the PinballWizard ActivitySource — every span is a silent
    // no-op and every metric is discarded, regardless of how
    // APPLICATIONINSIGHTS_CONNECTION_STRING is configured.
    //
    // StopAsync() is equally load-bearing: it triggers ForceFlush() on the
    // providers so batched telemetry already in memory is exported to Azure
    // Monitor before the process exits. A short-lived CLI job that skips
    // StopAsync loses all in-flight telemetry. The try/finally below
    // guarantees StopAsync runs on EVERY exit path — normal return, early
    // return from a verb handler, or an unhandled exception.
    //
    // DO NOT remove this call or the try/finally. This is the fix for #840.
    await host.StartAsync(cancellationToken);
    try
    {

    // Resolve the scraper orchestrator lazily/gracefully: its Cosmos-backed
    // dependencies are not activatable without Cosmos config, and utility verbs
    // (--eval, --corpus-coverage, --ensure-*, --gc-rag-index, etc.) do not need
    // it. GetService throws an activation exception (not null) when Cosmos is
    // unconfigured, so catch that and leave orchestrator null; the two scraper
    // paths (below) guard on null with a friendly remediation message.
    ScraperOrchestrator? orchestrator;
    try
    {
        orchestrator = host.Services.GetService<ScraperOrchestrator>();
    }
    catch (InvalidOperationException)
    {
        orchestrator = null;
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

    // Handle --gc-rag-index (orphan garbage collection — delete propagation
    // the Cosmos Change Feed can't provide). Resolves IRagIndexGarbageCollector;
    // registered only when AI Search is wired, and its scraped_documents
    // dependency additionally needs Cosmos (the early orchestrator gate above
    // already ensures Cosmos). Honors the shared --dry-run flag to preview.
    if (gcRagIndex)
    {
        var gc = host.Services.GetService<IRagIndexGarbageCollector>();
        if (gc is null)
        {
            Console.Error.WriteLine(
                "--gc-rag-index requires Cosmos and Azure AI Search to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos) and AiSearch:Endpoint.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(dryRun
            ? "RAG index GC (dry-run) starting — reporting orphan pairs without deleting..."
            : "RAG index GC starting — deleting orphan chunks with no backing scraped_documents row...");
        var gcResult = await gc.RunAsync(dryRun, cancellationToken);
        Console.WriteLine();
        Console.WriteLine(
            $"RAG index GC {(gcResult.DryRun ? "(dry-run) " : string.Empty)}complete: " +
            $"{gcResult.PairsScanned} pairs scanned, {gcResult.OrphanPairs} orphan pairs, " +
            $"{gcResult.ChunksDeleted} chunks deleted.");
        return;
    }

    // Handle --backfill-synthesized-raw-docs (heal dead synthesized citations by
    // writing a scraped_documents_raw row for any synthesized index document that
    // lacks one). Resolves SynthesizedRawDocBackfillService; registered only when AI
    // Search is wired, and its IRawDocumentRepository dependency additionally needs
    // Cosmos (the early orchestrator gate above already ensures Cosmos). Honors the
    // shared --dry-run flag to preview without writing.
    if (backfillSynthesizedRawDocs)
    {
        var backfill = host.Services.GetService<SynthesizedRawDocBackfillService>();
        if (backfill is null)
        {
            Console.Error.WriteLine(
                "--backfill-synthesized-raw-docs requires Cosmos and Azure AI Search to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos) and AiSearch:Endpoint.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(dryRun
            ? "Synthesized raw-doc backfill (dry-run) starting — reporting docs to write without writing..."
            : "Synthesized raw-doc backfill starting — writing raw docs for synthesized citations that lack one...");
        var backfillResult = await backfill.RunAsync(dryRun, cancellationToken);
        Console.WriteLine();
        Console.WriteLine(
            $"Synthesized raw-doc backfill {(backfillResult.DryRun ? "(dry-run) " : string.Empty)}complete: " +
            $"examined={backfillResult.Examined} written={backfillResult.Written} " +
            $"skipped_existing={backfillResult.SkippedExisting} skipped_unmapped={backfillResult.SkippedUnmapped} " +
            $"failed={backfillResult.Failed}.");
        // A write failure is caught and metered per-doc but must surface as a non-zero
        // exit so an ACA Job invocation doesn't report success while docs stayed dead
        // (invariant #17: degrade visibly).
        if (backfillResult.Failed > 0)
        {
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

    // Shared local function for the game-overview synthesis + index loop.
    // Called by both --sync-game-overviews and --refresh-game-overviews so the
    // loop body is not duplicated. Returns 0 on success, 1 if any chunk failed,
    // 2 if a required service is missing. Captures host + cancellationToken from
    // the enclosing action scope.
    async Task<int> RunGameOverviewSyncAsync()
    {
        var machineRepo = host.Services.GetService<IMachineRepository>();
        var synthesizer = host.Services.GetService<IGameOverviewSynthesizer>();
        var indexer = host.Services.GetService<IRagIndexer>();

        if (machineRepo is null || synthesizer is null || indexer is null)
        {
            Console.Error.WriteLine(
                "Game overview sync requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            return 2;
        }

        Console.WriteLine("Synthesizing game overview documents from Cosmos machines...");

        var upserted = 0;
        var skipped = 0;
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
                var chunks = synthesizer.Synthesize(machine);
                if (chunks.Count == 0 || string.IsNullOrWhiteSpace(machine.OverviewSourceUrl))
                {
                    skipped++;
                    continue;
                }

                var chunkRequest = new ChunkRequest(
                    MachineId: machine.Id,
                    MachineTitle: machine.Title,
                    Manufacturer: machine.ManufacturerDisplayName,
                    DocumentId: $"overview_{machine.Id}",
                    DocumentUrl: machine.OverviewSourceUrl,
                    DocumentType: DocumentType.GameOverview,
                    LastScrapedUtc: machine.LastSeenAt == default ? null : machine.LastSeenAt);

                try
                {
                    var result = await indexer.UpsertAsync(chunkRequest, chunks, indexerOptions, cancellationToken);
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
                    Console.Error.WriteLine($"  Failed to index game overview for {machine.Title} ({machine.Id}): {ex.Message}");
                    failed++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Game overview sync complete: upserted={upserted} skipped(no-content)={skipped} failed={failed}");
        return failed > 0 ? 1 : 0;
    }

    // Handle --sync-game-overviews (Phase 4.5 W4b — synthesize GameOverview chunks
    // from each Machine's OverviewProse + per-edition scraped content and upsert
    // into AI Search). Mirrors --sync-metadata-cards; gated on the same three
    // backend services. Skips machines with no overview content. Idempotent:
    // re-running overwrites in-place (chunk_id hash is stable for the same
    // machine + document key).
    if (syncGameOverviews)
    {
        Environment.ExitCode = await RunGameOverviewSyncAsync();
        return;
    }

    // Handle --refresh-game-overviews (atomic Stern game-page scrape + overview
    // sync in one pass). Scrapes the "games" source (Stern game pages, reconciling
    // Machine records), then calls RunGameOverviewSyncAsync to synthesize and index
    // GameOverview chunks. Equivalent to `--source games` then `--sync-game-overviews`
    // without re-launching the process. --dry-run runs the scrape but skips the sync.
    if (refreshGameOverviews)
    {
        if (orchestrator is null)
        {
            Console.Error.WriteLine(
                "Scraping requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire) or Cosmos:AccountEndpoint (production) in appsettings or environment, " +
                "then re-run. See docs/adr/0012-cosmos-arm-schema-data-plane-items.md for setup.");
            Environment.ExitCode = 2;
            return;
        }

        var refreshScrapeResult = await orchestrator.ScrapeAsync("games", dryRun, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"--refresh-game-overviews: scrape done ({refreshScrapeResult.TotalLinks} links discovered). Syncing overviews...");
        if (dryRun)
        {
            Console.WriteLine("--refresh-game-overviews: --dry-run, skipping overview sync.");
            return;
        }
        Environment.ExitCode = await RunGameOverviewSyncAsync();
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

    // Shared synonym file load for --ensure-machine-index and --rebuild-machine-index.
    // SeedPathResolver.Resolve walks up from AppContext.BaseDirectory so the path
    // works from both repo-root CLI invocations and bin-output dotnet run launches.
    // The guard in MachineSearchIndexBootstrapper.EnsureSynonymMapAsync logs a
    // Warning and skips the upsert when the text is empty (rather than sending an
    // empty body that AI Search would reject with 400).
    static async Task<string> LoadSynonymsTextAsync(CancellationToken ct)
    {
        const string relativePath = "data/seeds/machine_synonyms.v1.txt";
        var resolved = SeedPathResolver.Resolve(relativePath);
        return File.Exists(resolved)
            ? await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false)
            : string.Empty;
    }

    // Handle --ensure-machine-index (ADR-0049 phase 2a machine findability index
    // non-destructive ensure). Resolves MachineSearchIndexBootstrapper from DI;
    // registered when AddAzureAiSearchIntegration is wired (AiSearch:Endpoint set).
    // Does NOT project documents — use --rebuild-machine-index for a full rebuild.
    if (ensureMachineIndex)
    {
        var machineBootstrapper = host.Services.GetService<MachineSearchIndexBootstrapper>();

        if (machineBootstrapper is null)
        {
            Console.Error.WriteLine(
                "--ensure-machine-index requires Azure AI Search to be configured. " +
                $"Set {AiSearchOptions.EndpointKey}.");
            Environment.ExitCode = 2;
            return;
        }

        var synonymsText = await LoadSynonymsTextAsync(cancellationToken);
        Console.WriteLine("Ensuring machine search index schema and synonym map (non-destructive)...");
        var ensureResult = await machineBootstrapper.EnsureCreatedAsync(synonymsText, cancellationToken);
        Console.WriteLine(ensureResult.Created
            ? $"Machine search index created: {ensureResult.IndexName}"
            : $"Machine search index already present: {ensureResult.IndexName}");
        return;
    }

    // Handle --rebuild-machine-index (ADR-0049 phase 2a machine findability index
    // DESTRUCTIVE rebuild). Resolves MachineSearchIndexBootstrapper and
    // IMachineSearchIndexProjector from DI; both are only registered when
    // AddAzureAiSearchIntegration was wired (i.e., AiSearch:Endpoint is set).
    // Also requires Cosmos (IMachineRepository). Wipes ALL indexed documents;
    // use --ensure-machine-index for non-destructive schema/synonym updates.
    if (rebuildMachineIndex)
    {
        var machineBootstrapper = host.Services.GetService<MachineSearchIndexBootstrapper>();
        var machineProjector    = host.Services.GetService<IMachineSearchIndexProjector>();

        if (machineBootstrapper is null || machineProjector is null)
        {
            Console.Error.WriteLine(
                "--rebuild-machine-index requires Azure AI Search and Cosmos to be configured. " +
                $"Set {AiSearchOptions.EndpointKey} and Cosmos:AccountEndpoint (or ConnectionStrings:cosmos).");
            Environment.ExitCode = 2;
            return;
        }

        var synonymsText = await LoadSynonymsTextAsync(cancellationToken);
        Console.WriteLine("DESTRUCTIVE: dropping and recreating machine search index schema and synonym map...");
        var machineBootstrapResult = await machineBootstrapper.RecreateAsync(synonymsText, cancellationToken);
        Console.WriteLine(machineBootstrapResult.Created
            ? $"Machine search index created: {machineBootstrapResult.IndexName}"
            : $"Machine search index rebuilt: {machineBootstrapResult.IndexName}");

        Console.WriteLine("Projecting all machines from Cosmos into the machine search index...");
        var projectionResult = await machineProjector.ProjectAllAsync(cancellationToken);
        Console.WriteLine(
            $"Machine index rebuild complete: projected={projectionResult.Projected} " +
            $"failed={projectionResult.Failed} duration={projectionResult.Duration.TotalSeconds:N1}s");

        if (projectionResult.Failed > 0)
        {
            Console.Error.WriteLine($"  {projectionResult.Failed} documents failed — check logs for details.");
            Environment.ExitCode = 1;
        }
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

    // Handle --corpus-coverage. Resolves ICorpusCoverageProber (registered only
    // when AddAzureAiSearchIntegration was wired, i.e. AiSearch:Endpoint is set).
    // Writes a timestamped CoverageReport JSON and exits non-zero on gaps so the
    // scheduled workflow can alarm. No Foundry/Cosmos required.
    if (corpusCoverage)
    {
        var prober = host.Services.GetService<ICorpusCoverageProber>();
        if (prober is null)
        {
            Console.Error.WriteLine(
                $"--corpus-coverage requires AI Search to be configured. Set {AiSearchOptions.EndpointKey}.");
            Environment.ExitCode = 2;
            return;
        }

        var report = await prober.RunAsync(cancellationToken);

        PinballWizardTelemetry.RagCoverageCellsTotal.Add(report.CellsTotal);
        PinballWizardTelemetry.RagCoverageCellsCovered.Add(report.CellsCovered);
        PinballWizardTelemetry.RagCoverageGaps.Add(report.GapsTotal);
        PinballWizardTelemetry.RagCoverageRetrievabilityWarnings.Add(report.RetrievabilityWarnings);

        var resultsDir = Path.Combine("data", "eval", "results");
        Directory.CreateDirectory(resultsDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var path = Path.Combine(resultsDir, $"coverage.{stamp}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Corpus coverage: {report.CellsCovered}/{report.CellsTotal} cells retrievable, " +
                          $"{report.GapsTotal} source gaps, {report.RetrievabilityWarnings} retrievability warnings. " +
                          $"Report at {path}");
        foreach (var g in report.SourceGaps)
        {
            Console.WriteLine($"  SOURCE GAP: {g.Source} has zero indexed chunks (ExpectedNonEmpty).");
        }
        foreach (var w in report.Warnings)
        {
            Console.WriteLine($"  RETRIEVABILITY WARNING: {w.Source} / {w.DocumentType} not retrievable" +
                              (w.Error is null ? "." : $" ({w.Error})."));
        }

        if (report.GapsTotal > 0)
        {
            Environment.ExitCode = 1;
        }
        return;
    }

    // Handle --probe-retrieval (Phase 4.5 first-stage rank classifier; H5b
    // eval prep). Resolves IRetrievalRankProbe from DI; the probe is only
    // registered when AddAzureAiSearchIntegration was wired (i.e.,
    // AiSearch:Endpoint is set). Guards against reranker-on runs (would
    // corrupt the first-stage measurement) via CrossEncoderOptions.Enabled.
    // Mirrors the --eval exit-code-2 remediation pattern.
    if (!string.IsNullOrWhiteSpace(probeRetrieval))
    {
        await ProbeRetrievalCommand.RunAsync(probeRetrieval, host.Services, cancellationToken);
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

    // Handle --audit-catalog (proactive title-superset collision audit; #532
    // follow-up). Streams all machines and reports games whose title is an exact
    // game AND a subtitle-prefix of a different OPDB group — the catalog shape an
    // agent can mis-ground by dropping the subtitle. Read-only; exit code 3 when
    // collisions are found so CI/cron can alert.
    if (auditCatalog)
    {
        var auditRepo = host.Services.GetService<IMachineRepository>();
        if (auditRepo is null)
        {
            Console.Error.WriteLine(
                "--audit-catalog requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        var titlesAndGroups = new List<(string Title, string? GroupId)>();
        await foreach (var machine in auditRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            titlesAndGroups.Add((machine.Title, machine.GroupId));
        }

        var collisions = PinballWizard.Application.Catalog.TitleSupersetCollisionDetector.Detect(titlesAndGroups);
        Console.WriteLine(
            $"Catalog audit: scanned {titlesAndGroups.Count} machines; found {collisions.Count} title-superset collision(s).");

        if (collisions.Count > 0)
        {
            Console.WriteLine(
                "These titles are each an exact game AND a subtitle-prefix of a different game (a #532 mis-grounding risk —");
            Console.WriteLine(
                "ensure each has an eval question pinning the correct edition with franchise_wide_ok=false):");
            foreach (var c in collisions)
            {
                Console.WriteLine(
                    $"  '{c.ShorterTitle}' [{c.ShorterGroupId}]  ==>  '{c.LongerTitle}' [{c.LongerGroupId}]");
            }
            // Non-zero exit so a scheduled run / CI step surfaces the gap.
            Environment.ExitCode = 3;
        }

        return;
    }

    // Handle --sync-kineticist-tutorials (Domain-2 — index Kineticist gameplay
    // tutorials as Rulesheet docs in AI Search, ADR-0043). Mirrors
    // --sync-metadata-cards / --sync-game-overviews. Each tutorial is fetched as
    // clean Markdown via the .md URL suffix; machine linking uses
    // IMachineTitleLookupRepository; unresolvable slugs are logged + skipped
    // (visible degradation, not silent). Idempotent: chunk_id hash is stable for
    // the same article URL, so re-runs overwrite in place.
    if (syncKineticistTutorials)
    {
        var kineticistClient = host.Services.GetService<PinballWizard.Infrastructure.Scraping.Kineticist.KineticistTutorialsClient>();
        var kineticistSynthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.Kineticist.KineticistTutorialsSynthesizer>();
        var titleLookups = host.Services.GetService<IMachineTitleLookupRepository>();
        var kineticistIndexer = host.Services.GetService<IRagIndexer>();
        // ADR-0043 Tier A: OPDB-keyed linking via the Kineticist API. Optional
        // (registered only when an API key is configured); when absent the
        // legacy title-lookup path below is used.
        var kineticistResolver = host.Services.GetService<PinballWizard.Infrastructure.Integrations.Kineticist.IKineticistGameResolver>();
        var machineRepo = host.Services.GetService<IMachineRepository>();
        var kineticistOptions = host.Services.GetService<Microsoft.Extensions.Options.IOptions<PinballWizard.Core.Configuration.KineticistOptions>>();

        if (kineticistClient is null || kineticistSynthesizer is null || titleLookups is null || kineticistIndexer is null)
        {
            Console.Error.WriteLine(
                "--sync-kineticist-tutorials requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        var kineticistRawDocRepo = host.Services.GetService<IRawDocumentRepository>();

        Console.WriteLine("Discovering Kineticist tutorial articles...");

        var kineticistSlugs = await kineticistClient.DiscoverTutorialSlugsAsync(cancellationToken);
        Console.WriteLine($"Found {kineticistSlugs.Count} tutorial slug(s). Fetching and indexing...");

        var kineticistIndexed = 0;
        var kineticistEditionsLinked = 0;
        var kineticistSkippedNoMachine = 0;
        var kineticistSkippedNoContent = 0;
        var kineticistFailed = 0;
        var kineticistRawDocFailed = 0;
        var kineticistIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();

        foreach (var slug in kineticistSlugs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var article = await kineticistClient.FetchArticleAsync(slug, cancellationToken);
            if (article is null)
            {
                kineticistSkippedNoContent++;
                continue;
            }

            // Resolve the tutorial's target machine(s). Primary path (ADR-0043
            // Tier A): the Kineticist API maps the game to its OPDB-keyed
            // editions, which join to our catalog by OPDB id — no fuzzy title
            // matching. We link the rulesheet to EVERY edition we carry, since
            // gameplay is edition-agnostic. Fallback when no API key is
            // configured: the legacy single-machine title-lookup.
            var targets = new List<(string MachineId, string Title, string Manufacturer)>();

            // Link resolution touches the network (Kineticist API) and Cosmos.
            // Isolate per-tutorial: a transient API 5xx or repo error must skip
            // this one tutorial, not abort the whole run (degrade visibly).
            try
            {
                if (kineticistResolver is not null && machineRepo is not null
                    && !string.IsNullOrWhiteSpace(kineticistOptions?.Value.ApiKey))
                {
                    var match = await kineticistResolver.ResolveAsync(article.GameSlug, article.Title, cancellationToken);
                    if (match is not null)
                    {
                        var groupIds = match.EditionOpdbIds
                            .Select(id => id.Split('-', 2)[0])
                            .Where(g => !string.IsNullOrWhiteSpace(g))
                            .Distinct(StringComparer.OrdinalIgnoreCase);

                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var groupId in groupIds)
                        {
                            await foreach (var machine in machineRepo.GetSiblingsByGroupIdAsync(groupId, cancellationToken))
                            {
                                if (seen.Add(machine.Id))
                                {
                                    targets.Add((machine.Id, machine.Title, machine.ManufacturerDisplayName));
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Legacy fallback (no API key): title-lookup → first OPDB id only.
                    var lookupTitle = article.GameSlug.Replace('-', ' ');
                    var lookup = await titleLookups.GetByTitleAsync(lookupTitle, cancellationToken);
                    if (lookup is not null && lookup.OpdbIds.Count > 0)
                    {
                        var manu = lookup.Manufacturers.Count > 0 ? lookup.Manufacturers[0] : "Unknown";
                        targets.Add((lookup.OpdbIds[0], lookupTitle, manu));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Kineticist: link resolution failed for slug '{article.GameSlug}' ('{article.Title}'): {ex.Message}");
                kineticistFailed++;
                continue;
            }

            if (targets.Count == 0)
            {
                Console.Error.WriteLine(
                    $"  Kineticist: no machine in catalog for slug '{article.GameSlug}'; article '{article.Title}' skipped.");
                kineticistSkippedNoMachine++;
                continue;
            }

            var articleIndexed = false;
            var articleHadContent = false;
            foreach (var (machineId, machineTitle, machineManufacturer) in targets)
            {
                // Per-edition stable doc id: idempotent re-runs, and editions of
                // the same game don't collide on the same chunk id.
                var documentId = $"kineticist_{slug}_{machineId}";

                // Kineticist tutorials are gameplay rulesheets — edition-agnostic
                // per ADR-0032 — regardless of whether this article resolved to
                // one machine or fanned out to every sibling edition.
                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineId,
                    MachineTitle: machineTitle,
                    Manufacturer: machineManufacturer,
                    DocumentId: documentId,
                    DocumentUrl: article.CanonicalUrl,
                    DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                    LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow,
                    EditionScope: "franchise-wide");

                var chunks = kineticistSynthesizer.Synthesize(article, chunkRequest);
                if (chunks.Count == 0)
                {
                    continue;
                }
                articleHadContent = true;

                try
                {
                    var result = await kineticistIndexer.UpsertAsync(chunkRequest, chunks, kineticistIndexerOptions, cancellationToken);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                        {
                            Console.Error.WriteLine(
                                $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.Title}' → {machineId}: HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                        }
                        kineticistFailed++;
                    }
                    else
                    {
                        Console.WriteLine($"  Indexed '{article.Title}' ({article.Author}) → machine {machineId} ({chunks.Count} chunk(s))");
                        articleIndexed = true;
                        kineticistEditionsLinked++;

                        var kd = SynthesizedSourceDescriptors.Kineticist;
                        var synDoc = SynthesizedDocumentRecordFactory.Create(
                            documentId, article.Title, article.CanonicalUrl, kd.DiscoveryContext,
                            kd.DocumentType, kd.FileFormat, machineManufacturer,
                            machineTitle, article.GameSlug, article.PublishedAt ?? DateTimeOffset.UtcNow);
                        if (!await TryPersistSynthesizedRawDocAsync(kineticistRawDocRepo, synDoc, cancellationToken))
                        {
                            kineticistRawDocFailed++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Failed to index '{article.Title}' → {machineId}: {ex.Message}");
                    kineticistFailed++;
                }
            }

            if (articleIndexed)
            {
                kineticistIndexed++;
            }
            else if (!articleHadContent)
            {
                kineticistSkippedNoContent++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--sync-kineticist-tutorials complete: indexed={kineticistIndexed} editions_linked={kineticistEditionsLinked} skipped_no_machine={kineticistSkippedNoMachine} skipped_no_content={kineticistSkippedNoContent} failed={kineticistFailed} raw_doc_write_failed={kineticistRawDocFailed}");
        if (kineticistFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --sync-tiltforums-rulesheets (Domain-2 — index Tilt Forums
    // community rulesheets as Rulesheet docs in AI Search, ADR-0050).
    // Mirrors --sync-kineticist-tutorials: no Cosmos scraped_documents_raw
    // record, no change-feed, direct IRagIndexer.UpsertAsync. Game matching
    // is manufacturer-scoped (TiltForumsGameMatcher) rather than unscoped,
    // because Tilt Forums is cross-manufacturer, unlike every existing
    // single-manufacturer scraper.
    if (syncTiltForumsRulesheets)
    {
        var tiltForumsClient = host.Services.GetService<PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetsClient>();
        var tiltForumsSynthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetsSynthesizer>();
        var tiltForumsIndexer = host.Services.GetService<IRagIndexer>();
        var tiltForumsMachineRepo = host.Services.GetService<IMachineRepository>();
        var tiltForumsMachineSearchIndex = host.Services.GetService<IMachineSearchIndex>();

        if (tiltForumsClient is null || tiltForumsSynthesizer is null || tiltForumsIndexer is null || tiltForumsMachineRepo is null)
        {
            Console.Error.WriteLine(
                "--sync-tiltforums-rulesheets requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        var tiltForumsRawDocRepo = host.Services.GetService<IRawDocumentRepository>();

        Console.WriteLine("Discovering Tilt Forums rulesheets from the master list...");
        var masterListings = await tiltForumsClient.DiscoverRulesheetsAsync(cancellationToken);
        Console.WriteLine($"Found {masterListings.Count} rulesheet listing(s) in the master list.");

        if (masterListings.Count == 0)
        {
            Console.Error.WriteLine(
                "Tilt Forums master list returned 0 rulesheets — this likely indicates a fetch failure rather than a genuinely empty list; check the warning/error logs above.");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine("Cross-checking against the Wiki Rulesheets subcategory for additional topics...");
        var subcategoryListings = await tiltForumsClient.DiscoverSubcategoryRulesheetsAsync(cancellationToken);
        // Dedup by numeric topic id (not URL string) — Discourse serves the same topic under
        // multiple slugs (e.g. /t/stranger-things-rulesheet-wip/6093 vs .../6093), so
        // comparing full URLs would re-fetch already-covered topics.
        var masterListTopicIds = masterListings
            .Select(l => PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetsClient.TryParseTopicId(l.TopicUrl))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var subcategoryOnlyListings = subcategoryListings
            .Where(l =>
            {
                var id = PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetsClient.TryParseTopicId(l.TopicUrl);
                return id.HasValue
                    && !masterListTopicIds.Contains(id.Value)
                    && !l.TopicUrl.Contains("rulesheet-master-list", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        Console.WriteLine($"  {subcategoryOnlyListings.Count} subcategory-only topic(s) will be ingested this run.");
        var allListings = masterListings.Concat(subcategoryOnlyListings).ToList();

        var tiltForumsLogger = host.Services.GetService<ILoggerFactory>()?.CreateLogger("PinballWizard.Cli.TiltForumsRulesheetsSync");
        var tiltForumsIndexed = 0;
        var tiltForumsSkippedNoContent = 0;
        var tiltForumsUnmatched = 0;
        var tiltForumsFailed = 0;
        var tiltForumsRawDocFailed = 0;
        var tiltForumsEditionFamilyFanouts = 0;
        var tiltForumsFuzzyResolved = 0;
        var tiltForumsSubcategoryIndexed = 0;
        var tiltForumsIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();

        foreach (var listing in allListings)
        {
            if (cancellationToken.IsCancellationRequested) break;

            PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchResult matchResult;
            try
            {
                matchResult = await PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatcher.ResolveAsync(
                    tiltForumsMachineRepo, tiltForumsMachineSearchIndex, listing.GameTitle, listing.ManufacturerHeaderText, cancellationToken, tiltForumsLogger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: game matching failed for '{listing.GameTitle}' ({listing.ManufacturerHeaderText ?? "unscoped"}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            var isResolved = matchResult.Status is PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.Resolved
                or PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.ResolvedEditionFamily;
            if (!isResolved)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: unmatched '{listing.GameTitle}' ({listing.ManufacturerHeaderText ?? "unscoped"}) — {matchResult.Status}.");
                tiltForumsUnmatched++;
                continue;
            }

            if (matchResult.Status == PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.ResolvedEditionFamily)
            {
                tiltForumsEditionFamilyFanouts++;
            }

            if (matchResult.ResolvedViaFuzzy)
                tiltForumsFuzzyResolved++;

            PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetArticle? article;
            try
            {
                article = await tiltForumsClient.FetchRulesheetAsync(listing, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: fetch failed for '{listing.GameTitle}' ({listing.TopicUrl}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            if (article is null)
            {
                tiltForumsSkippedNoContent++;
                continue;
            }

            string topicId;
            try
            {
                topicId = new Uri(listing.TopicUrl).Segments[^1].TrimEnd('/');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: failed to parse topic URL for '{listing.GameTitle}' ({listing.TopicUrl}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            // Per-rulesheet flags, mirroring --sync-kineticist-tutorials's
            // articleIndexed/articleHadContent pattern: `indexed` counts once
            // per rulesheet that landed on at least one machine, not once per
            // sibling edition, so the two twin verbs' summary counters mean
            // the same thing.
            var rulesheetIndexed = false;
            var rulesheetHadContent = false;

            foreach (var machineMatch in matchResult.Machines)
            {
                var documentId = $"tiltforums_{topicId}_{machineMatch.MachineId}";

                // Rulesheets describe gameplay rules, which are edition-agnostic
                // (ADR-0032) — every chunk gets the franchise-wide tag regardless
                // of whether this listing resolved to one machine or fanned out
                // to several sibling editions.
                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineMatch.MachineId,
                    MachineTitle: machineMatch.MachineTitle,
                    Manufacturer: machineMatch.ManufacturerDisplayName,
                    DocumentId: documentId,
                    DocumentUrl: article.TopicUrl,
                    DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                    LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow,
                    EditionScope: "franchise-wide");

                var chunks = tiltForumsSynthesizer.Synthesize(article, chunkRequest);
                if (chunks.Count == 0)
                {
                    continue;
                }
                rulesheetHadContent = true;

                try
                {
                    var result = await tiltForumsIndexer.UpsertAsync(chunkRequest, chunks, tiltForumsIndexerOptions, cancellationToken);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                        {
                            Console.Error.WriteLine(
                                $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.GameTitle}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                        }
                        tiltForumsFailed++;
                    }
                    else
                    {
                        Console.WriteLine($"  Indexed '{article.GameTitle}' -> machine {machineMatch.MachineId} ({chunks.Count} chunk(s))");
                        rulesheetIndexed = true;

                        var td = SynthesizedSourceDescriptors.TiltForums;
                        var synDoc = SynthesizedDocumentRecordFactory.Create(
                            documentId, article.GameTitle, article.TopicUrl, td.DiscoveryContext,
                            td.DocumentType, td.FileFormat, machineMatch.ManufacturerDisplayName,
                            machineMatch.MachineTitle, null, article.PublishedAt ?? DateTimeOffset.UtcNow);
                        if (!await TryPersistSynthesizedRawDocAsync(tiltForumsRawDocRepo, synDoc, cancellationToken))
                        {
                            tiltForumsRawDocFailed++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Failed to index '{article.GameTitle}' -> machine {machineMatch.MachineId}: {ex.Message}");
                    tiltForumsFailed++;
                }
            }

            if (rulesheetIndexed)
            {
                tiltForumsIndexed++;
                if (string.IsNullOrWhiteSpace(listing.ManufacturerHeaderText))
                    tiltForumsSubcategoryIndexed++;
            }
            else if (!rulesheetHadContent)
            {
                tiltForumsSkippedNoContent++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"--sync-tiltforums-rulesheets complete: indexed={tiltForumsIndexed} unmatched={tiltForumsUnmatched} " +
            $"edition_family_fanouts={tiltForumsEditionFamilyFanouts} fuzzy_resolved={tiltForumsFuzzyResolved} " +
            $"skipped_no_content={tiltForumsSkippedNoContent} failed={tiltForumsFailed} raw_doc_write_failed={tiltForumsRawDocFailed} " +
            $"subcategory_indexed={tiltForumsSubcategoryIndexed}");
        if (tiltForumsFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --sync-twip-newsletter (Domain-2 — index TWIP newsletter issues as
    // NewsDigest docs in AI Search, ADR-0043). Discovers articles from the TWIP
    // sitemap (twip.kineticist.com/sitemap.xml) filtered by --twip-since date.
    // Uses synthetic machine_id="pinball_news" (no per-machine lookup needed).
    // Idempotent: chunk_id hash is stable for the same article URL, so re-runs
    // overwrite in place.
    if (syncTwipNewsletter)
    {
        var twipClient = host.Services.GetService<PinballWizard.Infrastructure.Scraping.Twip.TwipNewsletterClient>();
        var twipSynthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.Twip.TwipNewsletterSynthesizer>();
        var twipIndexer = host.Services.GetService<IRagIndexer>();

        if (twipClient is null || twipSynthesizer is null || twipIndexer is null)
        {
            Console.Error.WriteLine(
                "--sync-twip-newsletter requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        var twipRawDocRepo = host.Services.GetService<IRawDocumentRepository>();

        // Parse --twip-since date.
        DateTimeOffset? since = null;
        if (twipSince is not null)
        {
            if (!DateTimeOffset.TryParse(twipSince, out var parsedSince))
            {
                Console.Error.WriteLine(
                    $"--twip-since '{twipSince}' is not a valid ISO-8601 date. Expected format: YYYY-MM-DD.");
                Environment.ExitCode = 2;
                return;
            }
            since = parsedSince;
        }

        Console.WriteLine("Discovering TWIP newsletter articles from sitemap...");

        var twipSlugs = await twipClient.DiscoverArticleSlugsAsync(since, cancellationToken);
        Console.WriteLine($"Found {twipSlugs.Count} article slug(s). Fetching and indexing...");

        var twipIndexed = 0;
        var twipSkippedParse = 0;
        var twipSkippedContent = 0;
        var twipFailed = 0;
        var twipRawDocFailed = 0;
        var twipIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();

        foreach (var slug in twipSlugs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var article = await twipClient.FetchArticleAsync(slug, cancellationToken);
            if (article is null)
            {
                twipSkippedParse++;
                continue;
            }

            var documentId = $"twip_{slug}";
            var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                MachineId: "pinball_news",
                MachineTitle: "Pinball News",
                Manufacturer: "Kineticist",
                DocumentId: documentId,
                DocumentUrl: article.CanonicalUrl,
                DocumentType: PinballWizard.Core.Models.DocumentType.NewsDigest,
                LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow);

            var chunks = twipSynthesizer.Synthesize(article, chunkRequest);
            if (chunks.Count == 0)
            {
                twipSkippedContent++;
                continue;
            }

            try
            {
                var result = await twipIndexer.UpsertAsync(chunkRequest, chunks, twipIndexerOptions, cancellationToken);
                if (result.Failures.Count > 0)
                {
                    foreach (var failure in result.Failures)
                    {
                        Console.Error.WriteLine(
                            $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.Title}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                    }
                    twipFailed++;
                }
                else
                {
                    Console.WriteLine($"  Indexed '{article.Title}' ({article.Author}) → {chunks.Count} chunk(s)");
                    twipIndexed++;

                    var wd = SynthesizedSourceDescriptors.Twip;
                    var synDoc = SynthesizedDocumentRecordFactory.Create(
                        documentId, article.Title, article.CanonicalUrl, wd.DiscoveryContext,
                        wd.DocumentType, wd.FileFormat, wd.ManufacturerOverride!,
                        null, null, article.PublishedAt ?? DateTimeOffset.UtcNow);
                    if (!await TryPersistSynthesizedRawDocAsync(twipRawDocRepo, synDoc, cancellationToken))
                    {
                        twipRawDocFailed++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Failed to index '{article.Title}': {ex.Message}");
                twipFailed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--sync-twip-newsletter complete: discovered={twipSlugs.Count} indexed={twipIndexed} skipped_parse={twipSkippedParse} skipped_content={twipSkippedContent} failed={twipFailed} raw_doc_write_failed={twipRawDocFailed}");
        if (twipFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --sync-pb-freshdesk-articles: text-only Pinball Brothers
    // Freshdesk support articles (no PDF attachment) as SupportArticle chunks
    // in AI Search. Shares FreshdeskSolutionsClient's live crawl with
    // PbFreshdeskDocumentScraper (--source pb_freshdesk) but only processes
    // articles with zero attachments — attachment-bearing articles are that
    // scraper's job. Idempotent: chunk_id hash is stable per article URL.
    if (syncPbFreshdeskArticles)
    {
        var freshdeskClient = host.Services.GetService<PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.FreshdeskSolutionsClient>();
        var freshdeskSynthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.PbFreshdeskArticleSynthesizer>();
        var freshdeskTitleLookups = host.Services.GetService<IMachineTitleLookupRepository>();
        var freshdeskIndexer = host.Services.GetService<IRagIndexer>();

        if (freshdeskClient is null || freshdeskSynthesizer is null || freshdeskTitleLookups is null || freshdeskIndexer is null)
        {
            Console.Error.WriteLine(
                "--sync-pb-freshdesk-articles requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        var freshdeskRawDocRepo = host.Services.GetService<IRawDocumentRepository>();

        Console.WriteLine("Discovering Pinball Brothers Freshdesk support folders...");

        IReadOnlyList<PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.FreshdeskFolder> freshdeskFolders;
        try
        {
            freshdeskFolders = await freshdeskClient.DiscoverFoldersAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"--sync-pb-freshdesk-articles: folder discovery failed: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine($"Found {freshdeskFolders.Count} folder(s). Discovering articles...");

        var freshdeskIndexed = 0;
        var freshdeskSkippedAttachment = 0;
        var freshdeskSkippedNoContent = 0;
        var freshdeskSkippedNoMachine = 0;
        var freshdeskFailed = 0;
        var freshdeskRawDocFailed = 0;
        var freshdeskIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();
        var freshdeskKnownGameSlugs = PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.PbFreshdeskDocumentScraper.KnownGameSlugs;

        foreach (var folder in freshdeskFolders)
        {
            if (cancellationToken.IsCancellationRequested) break;

            IReadOnlyList<PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk.FreshdeskArticleSummary> summaries;
            try
            {
                summaries = await freshdeskClient.DiscoverArticlesInFolderAsync(folder, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Freshdesk: article discovery failed for folder '{folder.FolderName}': {ex.Message}; skipping folder.");
                freshdeskFailed++;
                continue;
            }

            foreach (var summary in summaries)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var article = await freshdeskClient.FetchArticleAsync(summary, cancellationToken);
                if (article is null)
                {
                    freshdeskSkippedNoContent++;
                    continue;
                }

                // Attachment-bearing articles are PbFreshdeskDocumentScraper's
                // job (--source pb_freshdesk) — this verb only handles the
                // text-only remainder.
                if (article.Attachments.Count > 0)
                {
                    freshdeskSkippedAttachment++;
                    continue;
                }

                var categoryLower = folder.CategoryName.ToLowerInvariant();
                var matchedSlug = freshdeskKnownGameSlugs.FirstOrDefault(s => categoryLower.Contains(s, StringComparison.Ordinal));

                string machineId, machineTitle, manufacturer;
                if (matchedSlug is not null)
                {
                    // Pinball Brothers' own game pages title themselves "<Name> Pinball"
                    // (e.g. "Queen Pinball", "Alien Pinball" — confirmed from this project's
                    // PbGamePageScraper fixtures), while the catalog's canonical OPDB title
                    // may or may not carry that suffix. Try both forms rather than a single
                    // guess, since MachineTitleLookup.NormalizeTitle does no suffix-stripping
                    // of its own — a bare-slug lookup would otherwise risk silently matching
                    // nothing for every article.
                    var displayName = matchedSlug switch
                    {
                        "abba" => "ABBA",
                        _ => char.ToUpperInvariant(matchedSlug[0]) + matchedSlug[1..],
                    };
                    string[] titleCandidates = [displayName, $"{displayName} Pinball"];

                    MachineTitleLookup? lookup = null;
                    foreach (var candidate in titleCandidates)
                    {
                        lookup = await freshdeskTitleLookups.GetByTitleAsync(candidate, cancellationToken);
                        if (lookup is not null && lookup.OpdbIds.Count > 0) break;
                    }

                    if (lookup is null || lookup.OpdbIds.Count == 0)
                    {
                        Console.Error.WriteLine(
                            $"  Freshdesk: no machine in catalog for '{matchedSlug}' (tried: {string.Join(", ", titleCandidates)}); article '{article.Title}' skipped.");
                        freshdeskSkippedNoMachine++;
                        continue;
                    }
                    machineId = lookup.OpdbIds[0];
                    machineTitle = displayName;
                    manufacturer = lookup.Manufacturers.Count > 0 ? lookup.Manufacturers[0] : "Pinball Brothers";
                }
                else
                {
                    // General-category article (FAQ, Getting Started, Warranty
                    // Terms) — not tied to a specific machine. Synthetic id
                    // mirrors TWIP's "pinball_news" pattern.
                    machineId = "pb_support";
                    machineTitle = "Pinball Brothers Support";
                    manufacturer = "Pinball Brothers";
                }

                var articleId = summary.Url.Split('/').Last().Split('-', 2)[0];
                var documentId = $"pb_freshdesk_{articleId}";

                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineId,
                    MachineTitle: machineTitle,
                    Manufacturer: manufacturer,
                    DocumentId: documentId,
                    DocumentUrl: article.Url,
                    DocumentType: PinballWizard.Core.Models.DocumentType.SupportArticle,
                    LastScrapedUtc: DateTimeOffset.UtcNow);

                var chunks = freshdeskSynthesizer.Synthesize(article, chunkRequest);
                if (chunks.Count == 0)
                {
                    freshdeskSkippedNoContent++;
                    continue;
                }

                try
                {
                    var result = await freshdeskIndexer.UpsertAsync(chunkRequest, chunks, freshdeskIndexerOptions, cancellationToken);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                        {
                            Console.Error.WriteLine(
                                $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.Title}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                        }
                        freshdeskFailed++;
                    }
                    else
                    {
                        Console.WriteLine($"  Indexed '{article.Title}' ({folder.FolderName}) → {chunks.Count} chunk(s)");
                        freshdeskIndexed++;

                        // Pass gameTitle/gameSlug only for game-specific articles; general-category
                        // articles (machineId == "pb_support") mirror the TWIP pinball_news pattern.
                        string? docGameTitle = machineId != "pb_support" ? machineTitle : (string?)null;
                        var fd = SynthesizedSourceDescriptors.PbFreshdesk;
                        var synDoc = SynthesizedDocumentRecordFactory.Create(
                            documentId, article.Title, article.Url, fd.DiscoveryContext,
                            fd.DocumentType, fd.FileFormat, manufacturer,
                            docGameTitle, matchedSlug, DateTimeOffset.UtcNow);
                        if (!await TryPersistSynthesizedRawDocAsync(freshdeskRawDocRepo, synDoc, cancellationToken))
                        {
                            freshdeskRawDocFailed++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Failed to index '{article.Title}': {ex.Message}");
                    freshdeskFailed++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--sync-pb-freshdesk-articles complete: indexed={freshdeskIndexed} skipped_attachment={freshdeskSkippedAttachment} skipped_no_content={freshdeskSkippedNoContent} skipped_no_machine={freshdeskSkippedNoMachine} failed={freshdeskFailed} raw_doc_write_failed={freshdeskRawDocFailed}");
        if (freshdeskFailed > 0)
            Environment.ExitCode = 1;
        return;
    }

    // Handle --sync-p3-sdk-docs (Multimorphic P3 SDK developer guides — SdkGuide
    // chunks in AI Search). Reads high-value text files from the local SDK zip
    // or extracted directory and synthesizes chunks via P3SdkDocsSynthesizer.
    // Skips the 1,032 Doxygen HTML files. Idempotent. Requires Azure AI Search
    // and Azure AI Foundry; does NOT require Cosmos (no scraped_documents path).
    if (syncP3SdkDocs)
    {
        var p3Synthesizer = host.Services.GetService<PinballWizard.Infrastructure.Scraping.P3Sdk.P3SdkDocsSynthesizer>();

        if (p3Synthesizer is null)
        {
            Console.Error.WriteLine(
                "--sync-p3-sdk-docs requires Azure AI Search and Azure AI Foundry to be configured. " +
                "Set AiSearch:Endpoint and AiFoundry:ProjectEndpoint in appsettings or environment.");
            Environment.ExitCode = 2;
            return;
        }

        var resolvedSdkPath = sdkPath ?? @"C:\earlybird\PinballWizard\P3_SDK_V0.9.zip";
        Console.WriteLine($"Indexing P3 SDK developer docs from: {resolvedSdkPath}");

        var p3Indexed = await p3Synthesizer.SyncAsync(resolvedSdkPath, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--sync-p3-sdk-docs complete: indexed={p3Indexed}");
        return;
    }

    // Handle --reclassify-documents (in-place classification fix for stored
    // scraped_documents_raw records; no HTTP calls; ADR-0042 follow-up).
    // Resolves IDocumentReclassifier from DI; the service is only registered
    // when AddCosmosPersistence was wired. Mirrors the --rebuild-catalog-stats
    // exit-code-2 remediation pattern.
    if (reclassifyDocuments)
    {
        await ReclassifyDocumentsCommand.RunAsync(host.Services, cancellationToken);
        return;
    }

    // Handle --backfill-manufacturer-slugs (issue #672: recover ManufacturerSlugs
    // from cross-reference provenance already in scraped_documents_raw; no HTTP
    // calls). Resolves IScraperReconciliationService + IRawDocumentRepository from
    // DI; both are only registered when Cosmos is configured.
    if (backfillManufacturerSlugs)
    {
        await BackfillManufacturerSlugsCommand.RunAsync(host.Services, cancellationToken);
        return;
    }

    // Handle --capture-golden-set (ADR-0054 S3 — read-only fixture capture for the
    // Wave-2 regression gate). Streams scraped_documents_raw where link_status is
    // Linked / ManuallyLinked and writes a JSON fixture + CAPTURE.md so that
    // GoldenLinkSetReplayTests can assert no mis-attribution offline (no Cosmos).
    // Operator-gated: run only after --relink-all against the fully re-linked corpus.
    if (captureGoldenSet)
    {
        await CaptureGoldenSetCommand.RunGoldenLinkSetAsync(host.Services, cancellationToken);
        return;
    }

    // Handle --capture-reconciler-parity (ADR-0054 S3 — read-only fixture capture for
    // the reconciler regression gate). Streams the machines container, records
    // ManufacturerSlugs per machine, and writes a per-manufacturer count snapshot +
    // CAPTURE.md so that ReconcilerParityReplayTests can assert the slug-match count
    // does not drop offline (no Cosmos). Operator-gated: run only after a full OPDB
    // sync + scraper reconciliation pass.
    if (captureReconcilerParity)
    {
        await CaptureGoldenSetCommand.RunReconcilerParityAsync(host.Services, cancellationToken);
        return;
    }

    // Handle --capture-page-text (#832 — read-only fixture capture for the page-tier
    // replay gate). Streams page-tier-linked documents, preview-extracts each blob's
    // first two pages, parity-truncates the text, and writes a JSON fixture +
    // CAPTURE-PAGE-TEXT.md so that PageTextLinkSetReplayTests can assert the linker's
    // page tiers reproduce the captured bindings offline (no Cosmos, no blob storage).
    // Operator-gated: run only after a deliberate re-link that you want as the baseline.
    if (capturePageText)
    {
        await CaptureGoldenSetCommand.RunPageTextSetAsync(host.Services, cancellationToken);
        return;
    }

    // Default behavior: scrape (discover + upsert to Cosmos).
    if (orchestrator is null)
    {
        Console.Error.WriteLine(
            "Scraping requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
            "(Aspire) or Cosmos:AccountEndpoint (production) in appsettings or environment, " +
            "then re-run. See docs/adr/0012-cosmos-arm-schema-data-plane-items.md for setup.");
        Environment.ExitCode = 2;
        return;
    }

    var scrapeResult = await orchestrator.ScrapeAsync(source, dryRun, cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Discovery: {scrapeResult.TotalLinks} links");

    if (!scrapeResult.Errors.IsEmpty)
    {
        Console.WriteLine($"  {scrapeResult.Errors.Count} errors during discovery");
        // A scraper failure is caught, logged, and added to Errors by
        // ScraperOrchestrator, then the run continues with the next source --
        // but without this, the process still exits 0. Every scraper now runs
        // as its own scheduled ACA Job (Admin > Jobs), and ACA reads the exit
        // code as the job's success/failure status: without this, a fully-failed
        // scraper run reports "Succeeded" on the dashboard (Invariant #17).
        Environment.ExitCode = 1;
    }

    } // end try: all verb handlers have run (or returned early)
    finally
    {
        // ForceFlush: exports any telemetry still buffered in the OTel pipeline
        // to Azure Monitor before the process terminates. Critical for a short-lived
        // CLI job — without this, all in-flight spans and metrics are discarded on
        // exit. This is paired with StartAsync() above; see #840 for the root cause.
        //
        // CancellationToken.None is deliberate, NOT an oversight. StopAsync's token
        // means "stop being graceful" — passing the run token would, on Ctrl+C or an
        // ACA SIGTERM, hand StopAsync an ALREADY-CANCELLED token and abandon the
        // flush at exactly the moment the telemetry matters most (a cancelled or
        // timed-out run is when you most want its traces), and would throw from this
        // finally block, masking the original outcome. The shutdown is still bounded
        // — HostOptions.ShutdownTimeout caps StopAsync independently of this token.
        await host.StopAsync(CancellationToken.None);
    }
});

// Honor Environment.ExitCode set by verb handlers; InvokeAsync returns 0 on a handled command, so a handler's ExitCode=2 would otherwise be lost.
var invokeExitCode = await rootCommand.Parse(args).InvokeAsync();
return invokeExitCode != 0 ? invokeExitCode : Environment.ExitCode;

// ── Shared synthesized-doc helpers ────────────────────────────────────────────

// Upserts a synthesized DocumentRecord to scraped_documents_raw and immediately
// sets its LinkStatus to PlatformGeneric so the linker skips it. Returns true on
// success (or when rawDocRepo is null — no Cosmos configured). Returns false and
// logs a warning on any transient error so callers can meter the failure without
// aborting the overall sync run (degrade-visibly, invariant #17).
static async Task<bool> TryPersistSynthesizedRawDocAsync(
    IRawDocumentRepository? rawDocRepo, DocumentRecord record, CancellationToken ct)
{
    if (rawDocRepo is null) return true; // no doc store configured — not a write failure
    try
    {
        await rawDocRepo.UpsertRawAsync(record, ct);
        await rawDocRepo.UpdateLinkStatusAsync(record.DocumentId, LinkStatus.PlatformGeneric, "synthesized", null, null, ct);
        return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"  Warning: raw-doc store write failed for {record.DocumentId}: {ex.Message}");
        return false;
    }
}

// ── Host Builder ──────────────────────────────────────────────────────────────

static IHost CreateHost(string[] args)
{
    // Before anything else touches a credential: opt-in Azure SDK tracing (#920).
    // Must precede AddServiceDefaults, which resolves SharedAzureCredential — token
    // acquisition events raised during that call are exactly what the listener is here
    // to capture, and a listener attached afterwards would miss them.
    AzureSdkDiagnostics.EnableIfConfigured();

    var builder = Host.CreateApplicationBuilder(args);

    // Aspire shared defaults — OpenTelemetry (logs / metrics / traces with the
    // OTLP exporter the AppHost dashboard injects via OTEL_EXPORTER_OTLP_ENDPOINT),
    // service discovery, standard HTTP resilience, and health checks. When the CLI
    // is launched standalone (no AppHost), these registrations are still safe — the
    // OTLP exporter only activates when the env var is present.
    // Credential: the process-wide UAMI (SharedAzureCredential.Instance) authenticates
    // the Azure Monitor exporters against pinwiz-ai-dev, which has DisableLocalAuth=true
    // and rejects key-based ingestion. One process-wide credential avoids the token-cache
    // contention that caused the 2026-06-11 eval outage (issue #362).
    builder.AddServiceDefaults(credential: SharedAzureCredential.Instance);

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

        // Document reclassifier (--reclassify-documents). Depends only on
        // IRawDocumentRepository (registered by AddCosmosPersistence above).
        // GetService<IDocumentReclassifier>() in the handler returns null
        // (→ exit code 2) when Cosmos is not configured.
        builder.Services.AddTransient<IDocumentReclassifier, DocumentReclassifier>();
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

    // Silverball Labs live-pricing integration — gated on API key presence (ADR-0045).
    // Absent key = IMarketValueProvider not registered; MarketValueTool degrades gracefully
    // (returns null, Wizard tells user live pricing is unavailable). Independent of Cosmos
    // and Foundry — the client is a plain typed HttpClient.
    var silverballWired = !string.IsNullOrWhiteSpace(builder.Configuration[SilverballLabsOptions.ApiKeyKey]);
    if (silverballWired)
    {
        builder.Services.AddSilverballLabsIntegration(builder.Configuration);
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

    // PDF text extractor — gated on Cosmos (cosmosWired) only.
    // PdfPigDocumentTextExtractor is a pure local PDF-parsing library with no
    // dependency on AI Search or Foundry. Registering it whenever Cosmos is
    // wired ensures DocumentLinker can exercise Tiers 3/4 (page-text matching)
    // whenever --link-documents / --relink-all is run with Cosmos configured —
    // regardless of whether the full RAG backfill stack (AI Search + Foundry) is
    // also present. Absent this registration, IDocumentTextExtractor is null,
    // Tiers 3/4 silently skip, and previously page-text-matched documents regress
    // to NotInCatalog with no operator warning (GitHub issue #654 / OBS-01).
    // The ADI-fallback upgrade path (FallbackDocumentTextExtractor) is governed
    // internally by DocumentIntelligence:Endpoint presence inside the method itself.
    if (cosmosWired)
    {
        builder.Services.AddPdfDocumentTextExtractor(builder.Configuration);
    }

    // RAG backfill service — gated on all three backend services being present.
    // Registers the full ingestion stack (pipeline + chunker + Cosmos-backed
    // IIndexState + backfill service) so `--run-rag-backfill` can populate the
    // AI Search index from existing scraped_documents without running the Change
    // Feed Processor. IDocumentTextExtractor is already registered above (cosmosWired
    // gate), so there is no double-registration risk (TryAddSingleton is idempotent).
    if (cosmosWired && aiSearchWired && foundryWired)
    {
        builder.Services.AddRagIngestionPipeline();
        builder.Services.AddHybridChunker();
        builder.Services.AddRagBackfillService(builder.Configuration);
        builder.Services.AddMetadataCardSynthesizer();
        builder.Services.AddGameOverviewSynthesizer();

        // Kineticist tutorials client + synthesizer (Domain-2 — ADR-0043). Fetches
        // gameplay tutorials via the .md URL suffix and synthesizes Rulesheet chunks
        // for AI Search indexing via --sync-kineticist-tutorials CLI verb.
        // Inside the RAG gate because KineticistTutorialsSynthesizer depends on
        // IChunker, which AddHybridChunker() registers (line above).
        builder.Services.AddKineticistScraping(builder.Configuration);
        builder.Services.AddTiltForumsScraping();

        // TWIP newsletter client + synthesizer (Domain-2 — ADR-0043). Fetches
        // newsletter articles via AngleSharp HTML parse and synthesizes NewsDigest
        // chunks for AI Search indexing via --sync-twip-newsletter CLI verb.
        // Inside the RAG gate because TwipNewsletterSynthesizer depends on
        // IChunker, which AddHybridChunker() registers (line above).
        builder.Services.AddTwipScraping(builder.Configuration);

        // P3 SDK docs synthesizer — indexes Multimorphic P3 SDK developer guides
        // (UsageInstructions + INSTALL.txt + ReleaseNotes.txt) as SdkGuide chunks
        // via --sync-p3-sdk-docs CLI verb. Inside the RAG gate because
        // P3SdkDocsSynthesizer depends on IChunker and IRagIndexer.
        builder.Services.AddTransient<PinballWizard.Infrastructure.Scraping.P3Sdk.P3SdkDocsSynthesizer>();
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
    builder.Services.AddPinballBrothersFreshdeskScraping(builder.Configuration);

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
