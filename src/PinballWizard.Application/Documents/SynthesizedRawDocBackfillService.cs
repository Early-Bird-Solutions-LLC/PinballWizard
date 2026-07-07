using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Documents;

public readonly record struct SynthesizedRawDocBackfillResult(
    int Examined,
    int Written,
    int SkippedExisting,
    int SkippedUnmapped,
    int Failed,
    bool DryRun);

// Heals orphaned synthesized citations. A synthesized document (Kineticist tutorial,
// Tilt Forums rulesheet, TWIP newsletter, PB-Freshdesk article) that is in the RAG
// index but has no scraped_documents_raw row resolves to "Document not found" at
// /documents/{id} — the citation link is dead. This happens for any synthesized doc
// indexed before the PR #701 write-path fix, and for docs whose live sync now skips
// re-indexing (e.g. the game slug no longer resolves to a machine) but whose stale
// index chunk — and therefore its citation — persists.
//
// The service scans the index for synthesized documents, and for each one MISSING a
// raw row writes a DocumentRecord reconstructed from the indexed metadata (title
// recovered from the chunk content, source url / type / manufacturer / freshness read
// straight from the index). It is idempotent and NON-destructive: a synthesized doc
// that already has a raw row (e.g. one written by the live sync with its original
// article title) is left untouched.
public sealed class SynthesizedRawDocBackfillService
{
    private readonly IIndexedSynthesizedDocumentSource _source;
    private readonly IRawDocumentRepository _rawDocRepo;
    private readonly ILogger<SynthesizedRawDocBackfillService> _logger;

    public SynthesizedRawDocBackfillService(
        IIndexedSynthesizedDocumentSource source,
        IRawDocumentRepository rawDocRepo,
        ILogger<SynthesizedRawDocBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rawDocRepo);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _rawDocRepo = rawDocRepo;
        _logger = logger;
    }

    public async Task<SynthesizedRawDocBackfillResult> RunAsync(
        bool dryRun, CancellationToken cancellationToken)
    {
        int examined = 0, written = 0, skippedExisting = 0, skippedUnmapped = 0, failed = 0;

        _logger.LogInformation(
            "Synthesized raw-doc backfill starting ({Mode}).", dryRun ? "dry-run" : "write");

        await foreach (var doc in _source
            .StreamSynthesizedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            examined++;

            var descriptor = SynthesizedSourceDescriptors.ForDocumentId(doc.DocumentId);
            if (descriptor is null)
            {
                // The source filters to synthesized prefixes, so this is defensive:
                // a prefix the source yielded but the descriptor table doesn't know.
                skippedUnmapped++;
                _logger.LogWarning(
                    "Backfill: document {DocumentId} has no synthesized-source descriptor — skipped.",
                    doc.DocumentId);
                continue;
            }

            var existing = await _rawDocRepo
                .GetAsync(doc.DocumentId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                skippedExisting++;
                continue;
            }

            var record = BuildRecord(doc, descriptor);

            if (dryRun)
            {
                written++;
                _logger.LogInformation(
                    "Backfill (dry-run): would write raw doc {DocumentId} title=\"{Title}\" type={Type}.",
                    record.DocumentId, record.Source.LinkText, record.Classification.DocumentType);
                continue;
            }

            try
            {
                await _rawDocRepo.UpsertRawAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex,
                    "Backfill: raw-doc write failed for {DocumentId} — continuing.", doc.DocumentId);
                continue;
            }

            // The row is written, so the citation already resolves — count it. Marking
            // it PlatformGeneric keeps the linker from trying (and failing) to resolve a
            // synthesized id, and the distinct "synthesized-backfill" strategy lets an
            // operator tell a backfilled row from a live-sync row ("synthesized"). If
            // ONLY this step fails the citation still resolves, so it is not a backfill
            // failure — but log it: the row keeps its default Pending status and the
            // linker will churn on it until a later pass repairs it (don't silently
            // leave that state unexplained — invariant #17).
            written++;
            try
            {
                await _rawDocRepo.UpdateLinkStatusAsync(
                    record.DocumentId, LinkStatus.PlatformGeneric, "synthesized-backfill", null, null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Backfill: wrote raw doc {DocumentId} but link-status update failed — " +
                    "row resolves, status left Pending.", record.DocumentId);
            }
            _logger.LogInformation(
                "Backfill: wrote raw doc {DocumentId} title=\"{Title}\".",
                record.DocumentId, record.Source.LinkText);
        }

        _logger.LogInformation(
            "Synthesized raw-doc backfill complete ({Mode}): examined={Examined} written={Written} " +
            "skippedExisting={SkippedExisting} skippedUnmapped={SkippedUnmapped} failed={Failed}.",
            dryRun ? "dry-run" : "write",
            examined, written, skippedExisting, skippedUnmapped, failed);

        return new SynthesizedRawDocBackfillResult(
            examined, written, skippedExisting, skippedUnmapped, failed, dryRun);
    }

    private static DocumentRecord BuildRecord(
        IndexedSynthesizedDocument doc, SynthesizedSourceDescriptor descriptor)
    {
        // Prefer the human title recovered from the indexed content; strip the
        // source's known suffix (Tilt Forums " — Rulesheet") so it matches what the
        // live sync would store. Fall back to the machine title, then the id, so the
        // detail page always has a non-empty heading.
        var title = CleanTitle(doc.Title, descriptor.ContentTitleSuffixToStrip);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = string.IsNullOrWhiteSpace(doc.MachineTitle) ? doc.DocumentId : doc.MachineTitle;
        }

        // Non-machine sources (TWIP news, PB general support) carry a synthetic
        // machine id and must not render a game reference.
        var hasGame = !SynthesizedSourceDescriptors.NonMachineMachineIds.Contains(doc.MachineId)
            && !string.IsNullOrWhiteSpace(doc.MachineTitle);

        var documentType = ParseDocumentType(doc.DocumentTypeName, descriptor.DocumentType);
        var manufacturer = descriptor.ManufacturerOverride ?? doc.Manufacturer;
        // last_scraped_utc is the synthesizer's original timestamp; fall back to now
        // only if the index chunk predates the freshness field (nullable, PR-C3).
        var synthesizedAt = doc.LastScrapedUtc ?? DateTimeOffset.UtcNow;

        return SynthesizedDocumentRecordFactory.Create(
            documentId: doc.DocumentId,
            title: title,
            sourceUrl: doc.DocumentUrl,
            discoveryContext: descriptor.DiscoveryContext,
            documentType: documentType,
            fileFormat: descriptor.FileFormat,
            manufacturer: manufacturer,
            gameTitle: hasGame ? doc.MachineTitle : null,
            gameSlug: null,
            synthesizedAt: synthesizedAt);
    }

    private static string? CleanTitle(string? title, string? suffixToStrip)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        var trimmed = title.Trim();
        if (!string.IsNullOrEmpty(suffixToStrip)
            && trimmed.EndsWith(suffixToStrip, StringComparison.Ordinal))
        {
            trimmed = trimmed[..^suffixToStrip.Length].TrimEnd();
        }
        return trimmed;
    }

    // The index stores document_type as the DocumentType enum name. Parse it back;
    // fall back to the descriptor's canonical type if a legacy chunk carried an
    // unrecognized string.
    private static DocumentType ParseDocumentType(string indexed, DocumentType fallback) =>
        Enum.TryParse<DocumentType>(indexed, ignoreCase: true, out var parsed) ? parsed : fallback;
}
