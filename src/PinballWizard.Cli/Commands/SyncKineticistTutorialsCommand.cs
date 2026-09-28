using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Integrations.Kineticist;
using PinballWizard.Infrastructure.Scraping.Kineticist;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Cli.Commands;

// --sync-kineticist-tutorials (Domain-2 — index Kineticist gameplay tutorials as
// Rulesheet docs in AI Search, ADR-0043). Each tutorial is fetched as clean
// Markdown via the .md URL suffix; machine linking uses the Kineticist API
// (OPDB-keyed) or IMachineTitleLookupRepository; unresolvable slugs are logged +
// skipped (visible degradation, not silent). Idempotent: chunk_id hash is stable
// for the same article URL, so re-runs overwrite in place.
//
// Exit codes: 0 = at least one tutorial indexed and no per-article failures;
// 1 = the source refused or failed us (discovery or mid-run), discovery found
// nothing, nothing was indexed, or any per-article failure; 2 = missing config.
internal static class SyncKineticistTutorialsCommand
{
    internal const int SourceFailureExitCode = 1;

    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var kineticistClient = services.GetService<KineticistTutorialsClient>();
        var kineticistSynthesizer = services.GetService<KineticistTutorialsSynthesizer>();
        var titleLookups = services.GetService<IMachineTitleLookupRepository>();
        var kineticistIndexer = services.GetService<IRagIndexer>();
        // ADR-0043 Tier A: OPDB-keyed linking via the Kineticist API. Optional
        // (registered only when an API key is configured); when absent the
        // legacy title-lookup path below is used.
        var kineticistResolver = services.GetService<IKineticistGameResolver>();
        var machineRepo = services.GetService<IMachineRepository>();
        var kineticistOptions = services.GetService<IOptions<KineticistOptions>>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SyncKineticistTutorialsCommand))
            ?? NullLogger.Instance;

        if (kineticistClient is null || kineticistSynthesizer is null || titleLookups is null || kineticistIndexer is null)
        {
            Console.Error.WriteLine(
                "--sync-kineticist-tutorials requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured. " +
                "Set Cosmos:AccountEndpoint (or ConnectionStrings:cosmos), AiSearch:Endpoint, and AiFoundry:ProjectEndpoint.");
            Environment.ExitCode = 2;
            return;
        }

        var kineticistRawDocRepo = services.GetService<IRawDocumentRepository>();

        Console.WriteLine($"Discovering Kineticist tutorial articles from {kineticistClient.NewsSitemapUrl} ...");

        IReadOnlyList<string> kineticistSlugs;
        try
        {
            kineticistSlugs = await kineticistClient.DiscoverTutorialSlugsAsync(cancellationToken);
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            ReportSourceFailure(logger, "discovery", kineticistClient.NewsSitemapUrl, ex);
            return;
        }

        if (kineticistSlugs.Count == 0)
        {
            logger.LogError(
                "Kineticist discovery found 0 tutorial slugs in {SitemapUrl}. The sitemap was read but no /news/{{slug}} " +
                "entry carries a 'tutorial' token — the sitemap shape or slug convention has changed. Failing the run " +
                "rather than reporting an empty sync as success.",
                kineticistClient.NewsSitemapUrl);
            Console.Error.WriteLine(
                $"--sync-kineticist-tutorials FAILED: 0 tutorial slugs discovered in {kineticistClient.NewsSitemapUrl}.");
            Environment.ExitCode = SourceFailureExitCode;
            return;
        }

        Console.WriteLine($"Found {kineticistSlugs.Count} tutorial slug(s). Fetching and indexing...");

        var kineticistIndexed = 0;
        var kineticistEditionsLinked = 0;
        var kineticistSkippedNoMachine = 0;
        var kineticistSkippedNoContent = 0;
        var kineticistFailed = 0;
        var kineticistRawDocFailed = 0;
        var kineticistIndexerOptions = new RagIndexerOptions();
        Exception? sourceFailure = null;
        string? sourceFailureSlug = null;

        foreach (var slug in kineticistSlugs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            KineticistTutorialArticle? article;
            try
            {
                article = await kineticistClient.FetchArticleAsync(slug, cancellationToken);
            }
            catch (PolitenessException ex)
            {
                // The host told us to stop (429 budget spent, Retry-After beyond
                // budget, or a bot challenge). Asking for the next article would
                // ignore that — stop the run here.
                sourceFailure = ex;
                sourceFailureSlug = slug;
                break;
            }

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
                var chunkRequest = new ChunkRequest(
                    MachineId: machineId,
                    MachineTitle: machineTitle,
                    Manufacturer: machineManufacturer,
                    DocumentId: documentId,
                    DocumentUrl: article.CanonicalUrl,
                    DocumentType: DocumentType.Rulesheet,
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
                        if (!await SynthesizedRawDocWriter.TryPersistAsync(kineticistRawDocRepo, synDoc, cancellationToken))
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
        Console.WriteLine($"--sync-kineticist-tutorials {(sourceFailure is null ? "complete" : "aborted")}: discovered={kineticistSlugs.Count} indexed={kineticistIndexed} editions_linked={kineticistEditionsLinked} skipped_no_machine={kineticistSkippedNoMachine} skipped_no_content={kineticistSkippedNoContent} failed={kineticistFailed} raw_doc_write_failed={kineticistRawDocFailed}");

        if (sourceFailure is not null)
        {
            ReportSourceFailure(logger, $"article '{sourceFailureSlug}'", (sourceFailure as PolitenessException)?.Url, sourceFailure);
            return;
        }

        if (kineticistIndexed == 0 && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Kineticist sync indexed 0 of {Discovered} discovered tutorials (skipped_no_content={NoContent}, " +
                "skipped_no_machine={NoMachine}, failed={Failed}). Failing the run rather than reporting an empty sync as success.",
                kineticistSlugs.Count, kineticistSkippedNoContent, kineticistSkippedNoMachine, kineticistFailed);
            Console.Error.WriteLine(
                $"--sync-kineticist-tutorials FAILED: 0 of {kineticistSlugs.Count} discovered tutorials were indexed.");
            Environment.ExitCode = SourceFailureExitCode;
            return;
        }

        if (kineticistFailed > 0)
            Environment.ExitCode = 1;
    }

    private static bool IsSourceFailure(Exception ex) =>
        ex is PolitenessException or HttpRequestException or InvalidDataException;

    private static void ReportSourceFailure(ILogger logger, string stage, Uri? url, Exception ex)
    {
        var target = (ex as PolitenessException)?.Url ?? url;
        var status = ex switch
        {
            PolitenessException pe => pe.Violation.ToString(),
            HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code} {code}",
            HttpRequestException => "network error",
            _ => ex.GetType().Name,
        };
        var remediation = ex is PolitenessException { Violation: PolitenessViolation.BotChallenge }
            ? " The host is serving a bot-protection challenge; retries cannot pass it. Ask the operator to allow the PinballWizard User-Agent (ADR-0043 contact), then re-run."
            : string.Empty;

        logger.LogError(ex,
            "Kineticist sync failed during {Stage}: {Url} → {Status}. {Detail}{Remediation}",
            stage, target, status, ex.Message, remediation);
        Console.Error.WriteLine(
            $"--sync-kineticist-tutorials FAILED during {stage}: {target} → {status}. {ex.Message}{remediation}");
        Environment.ExitCode = SourceFailureExitCode;
    }
}
