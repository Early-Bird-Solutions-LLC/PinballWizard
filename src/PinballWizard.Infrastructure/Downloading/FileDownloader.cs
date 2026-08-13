using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Downloading;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Downloading;

/// <summary>
/// Downloads files from manufacturer sites with conditional request support
/// (ETag/Last-Modified), SHA-256 hashing, and streaming to disk.
/// <para>
/// Polite-by-construction: every download routes through the shared
/// <see cref="IPolitenessGate"/> (robots.txt respect, per-origin throttle,
/// 429 backoff) exactly as <see cref="Scraping.Polite.PoliteScraperBase.SendPolitelyAsync"/>
/// does — this is the file-download analog of that base, since a download is
/// just as much an outbound request to a source site as a page scrape.
/// </para>
/// Transient-failure retry, jitter, backoff, and concurrency limiting are
/// owned by the resilience pipeline registered on this type's HttpClient in
/// the CLI's DI configuration (Microsoft.Extensions.Http.Resilience). This
/// class is the Infrastructure implementation of <see cref="IFileDownloader"/>;
/// the pipeline is the transport layer, the gate is the politeness layer.
/// </summary>
public sealed class FileDownloader : IFileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly IPolitenessGate _politeness;
    private readonly ScraperSettings _settings;
    private readonly ILogger<FileDownloader> _logger;

    public FileDownloader(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<ScraperSettings> settings,
        ILogger<FileDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(politeness);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _politeness = politeness;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Downloads a file if it has changed since the last download (using ETag/Last-Modified).
    /// Routes through the <see cref="IPolitenessGate"/> (robots.txt, per-origin throttle,
    /// 429 backoff). Retry of transient failures is performed by the resilience pipeline —
    /// by the time SendAsync returns here, retries (if any) are already exhausted.
    /// </summary>
    /// <remarks>
    /// A <see cref="PolitenessException"/> (robots.txt disallow, or a 429 streak that
    /// exceeds the configured maximum) is translated to a <see cref="DownloadStatus.PolitenessAbort"/>
    /// result — a deliberate "stop asking this origin" signal, distinct from a per-file
    /// <see cref="DownloadStatus.Failed"/>. The Infrastructure-only <see cref="PolitenessException"/>
    /// is deliberately NOT allowed to escape into the Application caller (Clean
    /// Architecture); the caller reasons over the abstract <see cref="DownloadStatus"/>
    /// it already owns and skips the rest of that origin.
    /// </remarks>
    public async Task<DownloadResult> DownloadAsync(
        string fileUrl,
        string localPath,
        HttpMetadata? previousMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(fileUrl);

        try
        {
            // Hold the politeness lease across the whole send: AcquireForRequestAsync
            // applies the per-origin delay + robots check before the request (throws
            // PolitenessException on a robots disallow), and disposing the lease stamps
            // "last request time" so the next download to this origin is paced.
            // Mirrors PoliteScraperBase.SendPolitelyAsync.
            await using var lease = await _politeness.AcquireForRequestAsync(uri, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);

            if (previousMetadata is not null)
            {
                if (previousMetadata.ETag is not null)
                    request.Headers.TryAddWithoutValidation("If-None-Match", previousMetadata.ETag);
                if (previousMetadata.LastModified.HasValue)
                    request.Headers.IfModifiedSince = new DateTimeOffset(previousMetadata.LastModified.Value);
            }

            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // Feed the response status into the gate's 429-streak tracker (and honor
            // any Retry-After) before we act on the status ourselves.
            await _politeness.ReportResponseAsync(
                uri, response.StatusCode, response.Headers.RetryAfter?.Delta, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.LogDebug("File not modified (304): {Url}", fileUrl);
                return new DownloadResult
                {
                    Status = DownloadStatus.NotModified,
                    FileUrl = fileUrl,
                    LocalPath = localPath
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusErr = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();

                // 403 Forbidden, 404 Not Found, and 410 Gone are permanent client-side
                // rejections — the resource is access-controlled, absent, or explicitly
                // removed at the origin. The resilience pipeline does not retry these
                // (they are not transient), and re-running the nightly job will not change
                // the outcome. Returning PermanentRejection signals the Application layer
                // to stamp a terminal skip record, keeping the job green and avoiding a
                // futile re-attempt on every subsequent run (#839, mirrors TooLarge #819).
                if (response.StatusCode is HttpStatusCode.Forbidden
                    or HttpStatusCode.NotFound
                    or HttpStatusCode.Gone)
                {
                    _logger.LogWarning("Permanent rejection for {Url}: {Status}", fileUrl, statusErr);
                    return new DownloadResult
                    {
                        Status = DownloadStatus.PermanentRejection,
                        FileUrl = fileUrl,
                        LocalPath = localPath,
                        ErrorMessage = statusErr
                    };
                }

                _logger.LogError("Download failed for {Url}: {Status}", fileUrl, statusErr);
                return new DownloadResult
                {
                    Status = DownloadStatus.Failed,
                    FileUrl = fileUrl,
                    LocalPath = localPath,
                    ErrorMessage = statusErr
                };
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > _settings.MaxFileSizeBytes)
            {
                var detail = $"{contentLength:N0} bytes exceeds MaxFileSizeBytes={_settings.MaxFileSizeBytes:N0}";
                _logger.LogWarning("File too large: {Url} — {Detail}", fileUrl, detail);
                return new DownloadResult
                {
                    Status = DownloadStatus.TooLarge,
                    FileUrl = fileUrl,
                    LocalPath = localPath,
                    SizeBytes = contentLength,
                    ErrorMessage = detail,
                };
            }

            // Stream to a DeleteOnClose temp file so SHA-256, size, and content are
            // all captured in a single pass without materializing the whole document
            // on the heap. The previous MemoryStream approach reasoned about ONE
            // document fitting comfortably in memory, but never accounted for
            // MaxConcurrentDownloads(3) simultaneous downloads, MemoryStream's
            // doubling growth transiently costing old+new buffers on the LOH, or
            // documents up to MaxFileSizeBytes(500 MB) — same analysis that drove
            // BlobDocumentStore to temp-file backing in #832. The caller
            // (DocumentDownloadService) disposes the returned stream after uploading
            // it to blob storage; DeleteOnClose ensures the temp file is removed at
            // that point (#836).
            //
            // DeleteOnClose on Linux unlinks at DISPOSE (SafeFileHandle.ReleaseHandle
            // "mimics" the flag), not at open — so a SIGKILL leaves the file. That is
            // acceptable by construction: ACA container-scoped ephemeral storage
            // disappears when the container shuts down or restarts, so an orphan can
            // never outlive the failed execution (mirrors the BlobDocumentStore comment).
            using var hash = SHA256.Create();
            long bytesWritten = 0;
            var buffer = new byte[81920];

            var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var contentBuffer = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                bool exceededDuringTransfer = false;

                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await contentBuffer.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        hash.TransformBlock(buffer, 0, bytesRead, null, 0);
                        bytesWritten += bytesRead;

                        if (bytesWritten > _settings.MaxFileSizeBytes)
                        {
                            exceededDuringTransfer = true;
                            break;
                        }
                    }
                }

                if (exceededDuringTransfer)
                {
                    await contentBuffer.DisposeAsync();  // DeleteOnClose removes the temp file
                    // Server omitted Content-Length so the pre-check above passed;
                    // discovered the cap breach mid-transfer. Same TooLarge semantics
                    // as the Content-Length path — permanent under the current cap.
                    var detail = $"{bytesWritten:N0} bytes exceeds MaxFileSizeBytes={_settings.MaxFileSizeBytes:N0}";
                    _logger.LogWarning("File exceeded cap during transfer (no Content-Length): {Url} — {Detail}", fileUrl, detail);
                    return new DownloadResult
                    {
                        Status = DownloadStatus.TooLarge,
                        FileUrl = fileUrl,
                        LocalPath = localPath,
                        SizeBytes = bytesWritten,
                        ErrorMessage = detail,
                    };
                }

                hash.TransformFinalBlock([], 0, 0);
                var sha256 = Convert.ToHexString(hash.Hash!).ToLowerInvariant();

                var httpMetadata = new HttpMetadata
                {
                    ETag = response.Headers.ETag?.Tag,
                    LastModified = response.Content.Headers.LastModified?.UtcDateTime,
                    ContentType = response.Content.Headers.ContentType?.MediaType,
                    ContentLength = bytesWritten
                };

                _logger.LogInformation("Downloaded {Size:N0} bytes: {Url} → {BlobName}",
                    bytesWritten, fileUrl, localPath);

                contentBuffer.Position = 0;
                return new DownloadResult
                {
                    Status = DownloadStatus.Downloaded,
                    FileUrl = fileUrl,
                    LocalPath = localPath,
                    Filename = Path.GetFileName(localPath),
                    SizeBytes = bytesWritten,
                    Sha256 = sha256,
                    Http = httpMetadata,
                    Content = contentBuffer  // temp FileStream; caller disposes → DeleteOnClose
                };
            }
            catch
            {
                // Network error, disk full, or cancellation — dispose the temp file
                // (DeleteOnClose removes it) and let the exception propagate to the
                // outer handler, which converts it to the appropriate DownloadResult.
                await contentBuffer.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — propagate without converting to a Failed result.
            throw;
        }
        catch (PolitenessException ex)
        {
            // Robots disallow or 429-streak abort for this origin. Translate to a
            // PolitenessAbort result (not Failed) so the Application caller can skip
            // the rest of THIS origin and continue with others — without the
            // Infrastructure-only PolitenessException leaking across the layer boundary.
            _logger.LogWarning(ex, "Politeness gate refused download for {Url} ({Violation})", fileUrl, ex.Violation);
            return new DownloadResult
            {
                Status = DownloadStatus.PolitenessAbort,
                FileUrl = fileUrl,
                LocalPath = localPath,
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // Narrow to the realistic download failure modes: network error
            // (HttpRequestException), disk/stream error (IOException), or
            // protocol mismatch (InvalidOperationException). All produce a
            // Failed result so the caller's loop can continue with other files.
            _logger.LogError(ex, "Failed to download: {Url}", fileUrl);
            return new DownloadResult
            {
                Status = DownloadStatus.Failed,
                FileUrl = fileUrl,
                LocalPath = localPath,
                ErrorMessage = ex.Message
            };
        }
    }
}

