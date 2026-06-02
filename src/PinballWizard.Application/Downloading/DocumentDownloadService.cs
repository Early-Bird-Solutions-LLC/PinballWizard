using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Downloading;

/// <summary>
/// Downloads every not-yet-downloaded document in <c>scraped_documents_raw</c>
/// so the linker's page-text tiers can read page-1 content for edition
/// resolution. Polite (the injected <see cref="IFileDownloader"/> routes every
/// download through the shared politeness gate — robots.txt, per-origin
/// throttle, 429 backoff — and owns the read timeout), idempotent (skips
/// documents that already have a local file), and provenance-preserving (only
/// the <c>File</c> field is written back).
/// </summary>
public sealed class DocumentDownloadService
{
    private readonly IRawDocumentRepository _repo;
    private readonly IFileDownloader _downloader;
    private readonly ILogger<DocumentDownloadService> _logger;

    public DocumentDownloadService(
        IRawDocumentRepository repo,
        IFileDownloader downloader,
        ILogger<DocumentDownloadService> logger)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(logger);
        _repo = repo;
        _downloader = downloader;
        _logger = logger;
    }

    public async Task<DownloadSummary> RunAsync(CancellationToken cancellationToken)
    {
        int downloaded = 0, skipped = 0, failed = 0;

        // Origins the politeness gate has told us to stop asking (robots disallow or
        // 429 streak). Once an origin is poisoned we skip its remaining documents but
        // keep downloading from every OTHER origin — a politeness abort on one source
        // must not abandon downloads for healthy sources. Mirrors how ScraperOrchestrator
        // isolates a source-level abort without failing the whole run.
        var poisonedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var raw in _repo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (raw.File?.LocalPath is not null) { skipped++; continue; }

            var fileUrl = raw.Source.FileUrl;
            if (string.IsNullOrEmpty(fileUrl)) { skipped++; continue; }

            var host = TryGetHost(fileUrl);
            if (host is not null && poisonedHosts.Contains(host)) { skipped++; continue; }

            // Pass the path RELATIVE to the downloads root — IFileDownloader owns the
            // root and combines it (so the persisted LocalPath stays portable across
            // environments, e.g. dev box vs ACA, rather than baking in an absolute path).
            var relPath = BuildLocalPath(raw, fileUrl);
            var result = await _downloader
                .DownloadAsync(fileUrl, relPath, raw.Http, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status is DownloadStatus.Downloaded or DownloadStatus.NotModified)
            {
                await _repo.UpdateFileAsync(raw.DocumentId, new DownloadedFileInfo
                {
                    LocalPath = result.LocalPath,
                    Filename = result.Filename ?? Path.GetFileName(result.LocalPath),
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
            "DocumentDownload complete: downloaded={Downloaded} skipped={Skipped} failed={Failed} poisonedOrigins={Poisoned}",
            downloaded, skipped, failed, poisonedHosts.Count);
        return new DownloadSummary(downloaded, skipped, failed);
    }

    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    // Lay downloads out as {sourceType}/{filename} under the downloads root so
    // each manufacturer's docs stay grouped and filenames don't collide globally.
    private static string BuildLocalPath(RawDocumentRecord raw, string fileUrl)
    {
        var sourceType = raw.Source.SourceType.ToString().ToLowerInvariant();
        var filename = Path.GetFileName(new Uri(fileUrl).AbsolutePath);
        return Path.Combine(sourceType, filename);
    }
}

/// <summary>Result counts from a <see cref="DocumentDownloadService"/> run.</summary>
public sealed record DownloadSummary(int Downloaded, int Skipped, int Failed);
