using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Documents;

// Streams all records from scraped_documents_raw, re-runs
// ScraperOrchestrator.ClassifyDocumentType over each record's stored
// Source fields (the same inputs used at first discovery), and writes
// back ONLY the records whose classification changed.
//
// Design invariants:
//   - NO external HTTP calls (operates on already-stored data).
//   - Provenance-preserving: only document_type is updated; Source,
//     Classification.FileFormat, Timeline, File, Http, CrossReferences,
//     and all linker metadata are left untouched.
//   - Idempotent: running twice produces the same result because the
//     second run re-classifies the already-updated type to itself.
//   - Degrade-visibly (invariant #17): per-document exceptions are caught,
//     logged, and metered; the run continues and returns a non-zero
//     Failed count rather than aborting.
//
// After a successful run the operator should run --relink-all so the
// linker fans the updated document_type from scraped_documents_raw into
// scraped_documents. The change-feed worker then re-ingests those
// documents with the new type, admitting e.g. Rulesheet docs that were
// previously classified as Other and filtered out.
public sealed class DocumentReclassifier : IDocumentReclassifier
{
    private readonly IRawDocumentRepository _repo;
    private readonly ILogger<DocumentReclassifier> _logger;

    public DocumentReclassifier(
        IRawDocumentRepository repo,
        ILogger<DocumentReclassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(logger);
        _repo = repo;
        _logger = logger;
    }

    public async Task<ReclassifyResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var scanned = 0;
        var reclassified = 0;
        var unchanged = 0;
        var failed = 0;
        var transitions = new List<ReclassifyTransition>();

        await foreach (var raw in _repo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PinballWizardTelemetry.ReclassifyScanned.Add(1);
            scanned++;

            try
            {
                // Reconstruct the DiscoveredLink from stored Source fields —
                // exactly what ScraperOrchestrator.BuildDocumentRecord received
                // at first-discovery time.
                var link = new DiscoveredLink
                {
                    FileUrl = raw.Source.FileUrl,
                    LinkText = raw.Source.LinkText,
                };

                var newType = ScraperOrchestrator.ClassifyDocumentType(
                    link,
                    raw.Source.DiscoveryContext,
                    raw.Source.SourceType);

                if (newType == raw.DocumentType)
                {
                    PinballWizardTelemetry.ReclassifyUnchanged.Add(1);
                    unchanged++;
                    continue;
                }

                // Classification changed — write back ONLY document_type.
                var oldType = raw.DocumentType.ToString();
                await _repo.UpdateDocumentTypeAsync(
                    raw.DocumentId,
                    newType,
                    cancellationToken).ConfigureAwait(false);

                PinballWizardTelemetry.ReclassifyChanged.Add(
                    1,
                    new KeyValuePair<string, object?>("old_type", oldType),
                    new KeyValuePair<string, object?>("new_type", newType.ToString()));

                reclassified++;
                transitions.Add(new ReclassifyTransition(
                    raw.DocumentId,
                    raw.DocumentUrl,
                    oldType,
                    newType.ToString()));

                _logger.LogInformation(
                    "Reclassified {DocumentId} ({DocumentUrl}): {OldType} → {NewType}",
                    raw.DocumentId, raw.DocumentUrl, oldType, newType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Degrade visibly (invariant #17): log + meter but continue.
                PinballWizardTelemetry.ReclassifyFailed.Add(1);
                failed++;
                _logger.LogError(
                    ex,
                    "Reclassify failed for document {DocumentId} ({DocumentUrl}) — skipping",
                    raw.DocumentId, raw.DocumentUrl);
            }
        }

        sw.Stop();
        PinballWizardTelemetry.ReclassifyDurationMs.Record(sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "--reclassify-documents complete: scanned={Scanned} reclassified={Reclassified} " +
            "unchanged={Unchanged} failed={Failed} elapsed={ElapsedMs}ms",
            scanned, reclassified, unchanged, failed, (long)sw.Elapsed.TotalMilliseconds);

        return new ReclassifyResult(scanned, reclassified, unchanged, failed, transitions);
    }
}
