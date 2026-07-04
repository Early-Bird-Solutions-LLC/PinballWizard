using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Downloading;

/// <summary>
/// Downloads every not-yet-downloaded document in <c>scraped_documents_raw</c>
/// so the linker's page-text tiers can read page-1 content for edition
/// resolution. Polite (the injected <see cref="IFileDownloader"/> routes every
/// download through the shared politeness gate — robots.txt, per-origin
/// throttle, 429 backoff — and owns the read timeout), idempotent (skips
/// documents whose blob already exists in <c>pinwiz-raw</c> — unless
/// <c>force</c> is set), and provenance-preserving (only the <c>File</c>
/// field is written back; <c>File.LocalPath</c> holds the blob name).
/// </summary>
public sealed class DocumentDownloadService
{
    private readonly IRawDocumentRepository _repo;
    private readonly IFileDownloader _downloader;
    private readonly IDocumentBlobStore _blobStore;
    private readonly ILogger<DocumentDownloadService> _logger;

    public DocumentDownloadService(
        IRawDocumentRepository repo,
        IFileDownloader downloader,
        IDocumentBlobStore blobStore,
        ILogger<DocumentDownloadService> logger)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(blobStore);
        ArgumentNullException.ThrowIfNull(logger);
        _repo = repo;
        _downloader = downloader;
        _blobStore = blobStore;
        _logger = logger;
    }

    /// <param name="force">
    /// When true, re-download every document even if its raw record already carries a
    /// <c>File.LocalPath</c>, and issue an UNCONDITIONAL GET (ignoring the stored
    /// ETag / Last-Modified). This is the backfill path: the recorded LocalPath may point
    /// at a file produced by an earlier, ephemeral run (e.g. an ACA job's /tmp) that does
    /// not exist on the machine running the linker, so a conditional GET could return 304
    /// NotModified and leave no local file for the page-1 edition tier to read. Forcing a
    /// full fetch guarantees the bytes land locally. Still fully polite — every request
    /// routes through the same politeness gate.
    /// </param>
    public async Task<DownloadSummary> RunAsync(bool force, CancellationToken cancellationToken)
    {
        int downloaded = 0, skipped = 0, failed = 0, backfilled = 0;

        // Origins the politeness gate has told us to stop asking (robots disallow or
        // 429 streak). Once an origin is poisoned we skip its remaining documents but
        // keep downloading from every OTHER origin — a politeness abort on one source
        // must not abandon downloads for healthy sources. Mirrors how ScraperOrchestrator
        // isolates a source-level abort without failing the whole run.
        var poisonedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var raw in _repo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileUrl = raw.Source.FileUrl;
            if (string.IsNullOrEmpty(fileUrl)) { skipped++; continue; }

            // Blob name = the relative path used as the durable storage key.
            // Computed here (before the skip check) so ExistsAsync can use it
            // as the source-of-truth "already stored" signal — blob presence is
            // durable across ACA runs; File.LocalPath may reference a blob key
            // from a prior run that still exists in pinwiz-raw.
            var blobName = BuildBlobName(raw, fileUrl);

            if (!force)
            {
                var hasLocalPath = raw.File?.LocalPath is not null;
                // Whitespace-checked, not null-checked, to match the exact guard
                // CosmosRawDocumentRepository.UpdateFileAsync uses before copying
                // Sha256 into ContentHash. A mismatched check here (e.g. treating
                // Sha256 = "" as "known") would make this branch fire every run
                // without ever satisfying hasContentHash — a permanent backfill loop.
                var hasSha256 = !string.IsNullOrWhiteSpace(raw.File?.Sha256);
                var hasContentHash = !string.IsNullOrWhiteSpace(raw.ContentHash);

                // Fast path: LocalPath, Sha256, AND the top-level ContentHash all
                // already set — genuinely nothing to do.
                if (hasLocalPath && hasSha256 && hasContentHash) { skipped++; continue; }

                // Denormalization-only self-heal (issue #664, second layer): Sha256 is
                // already known (a prior download or the self-heal branch below
                // computed it), but the top-level ContentHash was never copied from
                // it — the only other writer of ContentHash is UpsertRawAsync (the
                // scraper's re-discovery path), so a document downloaded but not yet
                // re-scraped a second time stays stuck here. No blob read needed: the
                // hash is already known. Uses DenormalizeContentHashAsync (not
                // UpdateFileAsync) so Timeline.LastDownloadedAt is NOT touched — no
                // bytes were transferred, so stamping "now" would misrepresent when
                // this document was actually last fetched.
                if (hasLocalPath && hasSha256 && !hasContentHash)
                {
                    await _repo.DenormalizeContentHashAsync(raw.DocumentId, raw.File!.Sha256!, cancellationToken)
                        .ConfigureAwait(false);
                    backfilled++;
                    skipped++;
                    continue;
                }

                // Durability check: the blob may already exist in pinwiz-raw even
                // though Cosmos was never stamped (e.g. an earlier run wrote the
                // blob then crashed/was interrupted before the Cosmos write). This
                // is a desync, not a "nothing to do" — self-heal it here, mirroring
                // the DownloadStatus.NotModified branch below (which also stamps
                // Cosmos without new bytes). Without this, the record stays
                // permanently desynced: every future run re-hits this same check
                // and skips again, silently blocking the linker's page-text tiers
                // for a document whose content is right there in storage (issue #661).
                if (await _blobStore.ExistsAsync(blobName, cancellationToken).ConfigureAwait(false))
                {
                    var size = await _blobStore.GetSizeAsync(blobName, cancellationToken).ConfigureAwait(false);
                    if (size is null)
                    {
                        // TOCTOU: the blob answered Exists=true but is gone (or errored)
                        // by the time we asked for its properties. Stamping SizeBytes=0
                        // and moving on is the least-bad outcome (still better than
                        // leaving the record desynced forever), but it must be visible —
                        // a future --force re-download is the recovery path.
                        _logger.LogWarning(
                            "DocumentDownload: backfill for {DocId} — GetSizeAsync returned null for " +
                            "'{BlobName}' immediately after ExistsAsync=true; stamping SizeBytes=0. " +
                            "Use --force-redownload to recover if the blob is genuinely gone.",
                            raw.DocumentId, blobName);
                    }

                    // Only the File field is written back (provenance invariant).
                    // Sha256 IS computed here — from the already-stored blob, not by
                    // re-downloading from the source — because this branch stamps
                    // LocalPath, and every future run hits the fast-path skip above
                    // (raw.File?.LocalPath is not null) without ever revisiting this
                    // document. If Sha256 stayed null here it would stay permanently
                    // null: ContentHash would never be set, so RAG's rag_index_state
                    // hash short-circuit could never engage for this document, and it
                    // would be fully re-embedded on every future --run-rag-backfill run,
                    // forever (issue #664). Reading our own blob storage to hash it is
                    // cheap (no external HTTP call to the source) — unlike a real
                    // re-download, which this self-heal path exists specifically to avoid.
                    string? sha256 = null;
                    await using (var blobStream = await _blobStore
                        .TryOpenReadAsync(blobName, cancellationToken).ConfigureAwait(false))
                    {
                        if (blobStream is not null)
                        {
                            var hashBytes = await SHA256.HashDataAsync(blobStream, cancellationToken)
                                .ConfigureAwait(false);
                            sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                        }
                        else
                        {
                            // Same TOCTOU window as the GetSizeAsync race above: the blob
                            // answered Exists=true but is gone by the time we read it.
                            // Degrade to a null Sha256 rather than throw — --force-redownload
                            // remains the recovery path.
                            _logger.LogWarning(
                                "DocumentDownload: backfill for {DocId} — TryOpenReadAsync returned null for " +
                                "'{BlobName}' immediately after ExistsAsync=true; leaving Sha256 unset.",
                                raw.DocumentId, blobName);
                        }
                    }

                    await _repo.UpdateFileAsync(raw.DocumentId, new DownloadedFileInfo
                    {
                        LocalPath = blobName,
                        Filename = Path.GetFileName(blobName),
                        SizeBytes = size ?? 0,
                        Sha256 = sha256,
                    }, cancellationToken).ConfigureAwait(false);
                    backfilled++;
                    skipped++;
                    continue;
                }
            }

            var host = TryGetHost(fileUrl);
            if (host is not null && poisonedHosts.Contains(host)) { skipped++; continue; }

            // Force ⇒ unconditional GET (null previousMetadata): see the RunAsync param doc —
            // a 304 NotModified could leave no bytes in pinwiz-raw for the page-1 tier.
            var previousMetadata = force ? null : raw.Http;
            var result = await _downloader
                .DownloadAsync(fileUrl, blobName, previousMetadata, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status is DownloadStatus.Downloaded)
            {
                // result.Content holds the downloaded bytes; write them to the durable
                // blob store so they survive across ACA executions (ephemeral /tmp).
                // WriteAsync overwrites any stale blob when force is true.
                // Content must be set for Downloaded status — IFileDownloader contract.
                if (result.Content is null)
                    throw new InvalidOperationException(
                        $"IFileDownloader returned Downloaded status with no Content stream for {fileUrl}");
                await using (result.Content)
                {
                    await _blobStore.WriteAsync(blobName, result.Content, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Only the File field is written back (provenance invariant); blob name
                // becomes the durable LocalPath reference.
                await _repo.UpdateFileAsync(raw.DocumentId, new DownloadedFileInfo
                {
                    LocalPath = blobName,
                    Filename = result.Filename ?? Path.GetFileName(blobName),
                    SizeBytes = result.SizeBytes ?? 0,
                    Sha256 = result.Sha256,
                }, cancellationToken).ConfigureAwait(false);
                downloaded++;
            }
            else if (result.Status is DownloadStatus.NotModified)
            {
                // 304 means the server confirmed the blob is current. Stamp the record
                // if it didn't already have this blobName — preserves idempotency for
                // records whose LocalPath was written by a previous run but whose Http
                // metadata matched. No blob write needed (blob already current).
                await _repo.UpdateFileAsync(raw.DocumentId, new DownloadedFileInfo
                {
                    LocalPath = blobName,
                    Filename = result.Filename ?? Path.GetFileName(blobName),
                    SizeBytes = result.SizeBytes ?? 0,
                    Sha256 = result.Sha256,
                }, cancellationToken).ConfigureAwait(false);
                downloaded++;
            }
            else if (result.Status is DownloadStatus.PolitenessAbort)
            {
                // Stop asking this origin; its remaining docs will be skipped above.
                if (host is not null) { poisonedHosts.Add(host); }
                _logger.LogWarning(
                    "DocumentDownload: politeness abort for {Host} ({DocId}): {Err} — skipping remaining docs on this origin",
                    host ?? "<unknown>", raw.DocumentId, result.ErrorMessage);
                skipped++;
            }
            else
            {
                _logger.LogWarning("DocumentDownload: {DocId} failed ({Status}): {Err}",
                    raw.DocumentId, result.Status, result.ErrorMessage);
                failed++;
            }
        }

        _logger.LogInformation(
            "DocumentDownload complete: downloaded={Downloaded} skipped={Skipped} failed={Failed} backfilled={Backfilled} poisonedOrigins={Poisoned}",
            downloaded, skipped, failed, backfilled, poisonedHosts.Count);
        return new DownloadSummary(downloaded, skipped, failed, backfilled);
    }

    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    // Blob name = {sourceType}/{filename} — groups each manufacturer's documents
    // and avoids global filename collisions. The forward-slash separator is
    // intentional: blob names use '/' as a virtual directory delimiter regardless
    // of the OS running the downloader (ACA Linux vs. dev Windows).
    private static string BuildBlobName(RawDocumentRecord raw, string fileUrl)
    {
        var sourceType = raw.Source.SourceType.ToString().ToLowerInvariant();
        var filename = Path.GetFileName(new Uri(fileUrl).AbsolutePath);
        return $"{sourceType}/{filename}";
    }
}

/// <summary>
/// Result counts from a <see cref="DocumentDownloadService"/> run.
/// <paramref name="Backfilled"/> counts records whose Cosmos <c>File</c> field
/// was stamped from an already-existing blob (self-heal) rather than a fresh
/// download — a subset of <paramref name="Skipped"/>, not a separate total.
/// </summary>
public sealed record DownloadSummary(int Downloaded, int Skipped, int Failed, int Backfilled = 0);
