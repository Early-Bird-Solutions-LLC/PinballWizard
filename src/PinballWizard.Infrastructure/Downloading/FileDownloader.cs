using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Downloading;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Downloading;

/// <summary>
/// Downloads files from manufacturer sites with conditional request support
/// (ETag/Last-Modified), SHA-256 hashing, and streaming to disk.
/// Transient-failure retry, jitter, backoff, and concurrency limiting are
/// owned by the resilience pipeline registered on this type's HttpClient in
/// the CLI's DI configuration (Microsoft.Extensions.Http.Resilience). This
/// class is the Infrastructure implementation of <see cref="IFileDownloader"/>;
/// the pipeline is the transport layer.
/// </summary>
public sealed class FileDownloader : IFileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private readonly ILogger<FileDownloader> _logger;

    public FileDownloader(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<FileDownloader> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Downloads a file if it has changed since the last download (using ETag/Last-Modified).
    /// Retry of transient failures is performed by the resilience pipeline — by the time
    /// SendAsync returns here, retries (if any) are already exhausted.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string fileUrl,
        string localPath,
        HttpMetadata? previousMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = Path.Combine(_settings.DownloadsPath, localPath);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);

            if (previousMetadata is not null)
            {
                if (previousMetadata.ETag is not null)
                    request.Headers.TryAddWithoutValidation("If-None-Match", previousMetadata.ETag);
                if (previousMetadata.LastModified.HasValue)
                    request.Headers.IfModifiedSince = new DateTimeOffset(previousMetadata.LastModified.Value);
            }

            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);

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
                _logger.LogWarning("File too large ({Size:N0} bytes), skipping: {Url}",
                    contentLength, fileUrl);
                return new DownloadResult
                {
                    Status = DownloadStatus.TooLarge,
                    FileUrl = fileUrl,
                    LocalPath = localPath
                };
            }

            var directory = Path.GetDirectoryName(absolutePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            using var hash = SHA256.Create();
            long bytesWritten = 0;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hash.TransformBlock(buffer, 0, bytesRead, null, 0);
                    bytesWritten += bytesRead;

                    if (bytesWritten > _settings.MaxFileSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"File exceeded max size during download: {bytesWritten:N0} bytes");
                    }
                }
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

            _logger.LogInformation("Downloaded {Size:N0} bytes: {Url} → {Path}",
                bytesWritten, fileUrl, localPath);

            return new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = fileUrl,
                LocalPath = localPath,
                Filename = Path.GetFileName(localPath),
                SizeBytes = bytesWritten,
                Sha256 = sha256,
                Http = httpMetadata
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — propagate without converting to a Failed result.
            throw;
        }
        catch (Exception ex)
        {
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

