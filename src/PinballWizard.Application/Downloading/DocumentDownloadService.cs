using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Downloading;

/// <summary>
/// Downloads every not-yet-downloaded document in <c>scraped_documents_raw</c>
/// so the linker's page-text tiers can read page-1 content for edition
/// resolution. Polite (the injected <see cref="IFileDownloader"/> routes
/// through the politeness gate and owns the read timeout), idempotent (skips
/// documents that already have a local file), and provenance-preserving (only
/// the <c>File</c> field is written back).
/// </summary>
public sealed class DocumentDownloadService
{
    private readonly IRawDocumentRepository _repo;
    private readonly IFileDownloader _downloader;
    private readonly ILogger<DocumentDownloadService> _logger;
    private readonly string _downloadsRoot;

    public DocumentDownloadService(
        IRawDocumentRepository repo,
        IFileDownloader downloader,
        ILogger<DocumentDownloadService> logger,
        string downloadsRoot)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(downloadsRoot);
        _repo = repo;
        _downloader = downloader;
        _logger = logger;
        _downloadsRoot = downloadsRoot;
    }

    public async Task<DownloadSummary> RunAsync(CancellationToken cancellationToken)
    {
        int downloaded = 0, skipped = 0, failed = 0;

        await foreach (var raw in _repo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (raw.File?.LocalPath is not null) { skipped++; continue; }

            var fileUrl = raw.Source.FileUrl;
            if (string.IsNullOrEmpty(fileUrl)) { skipped++; continue; }

            var relPath = BuildLocalPath(raw, fileUrl);
            var result = await _downloader
                .DownloadAsync(fileUrl, Path.Combine(_downloadsRoot, relPath), raw.Http, cancellationToken)
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
            else
            {
                _logger.LogWarning("DocumentDownload: {DocId} failed ({Status}): {Err}",
                    raw.DocumentId, result.Status, result.ErrorMessage);
                failed++;
            }
        }

        _logger.LogInformation(
            "DocumentDownload complete: downloaded={Downloaded} skipped={Skipped} failed={Failed}",
            downloaded, skipped, failed);
        return new DownloadSummary(downloaded, skipped, failed);
    }

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
