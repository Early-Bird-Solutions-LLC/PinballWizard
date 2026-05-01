using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Downloading;

/// <summary>
/// Downloads files from sternpinball.com with conditional request support (ETag/Last-Modified),
/// SHA-256 hashing, and streaming to disk.
/// </summary>
public sealed class FileDownloader
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
    /// Returns the download result with file metadata.
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

            // Add conditional request headers if we have previous metadata
            if (previousMetadata is not null)
            {
                if (previousMetadata.ETag is not null)
                    request.Headers.TryAddWithoutValidation("If-None-Match", previousMetadata.ETag);
                if (previousMetadata.LastModified.HasValue)
                    request.Headers.IfModifiedSince = new DateTimeOffset(previousMetadata.LastModified.Value);
            }

            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // 304 Not Modified — file hasn't changed
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                _logger.LogDebug("File not modified (304): {Url}", fileUrl);
                return new DownloadResult
                {
                    Status = DownloadStatus.NotModified,
                    FileUrl = fileUrl,
                    LocalPath = localPath
                };
            }

            response.EnsureSuccessStatusCode();

            // Check content length before downloading
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

            // Ensure directory exists
            var directory = Path.GetDirectoryName(absolutePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            // Stream to disk while computing SHA-256
            var hash = SHA256.Create();
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

                    // Safety check during download
                    if (bytesWritten > _settings.MaxFileSizeBytes)
                    {
                        hash.Dispose();
                        throw new InvalidOperationException(
                            $"File exceeded max size during download: {bytesWritten:N0} bytes");
                    }
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            var sha256 = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            hash.Dispose();

            // Extract HTTP metadata for future conditional requests
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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

public enum DownloadStatus
{
    Downloaded,
    NotModified,
    TooLarge,
    Failed
}

public sealed class DownloadResult
{
    public required DownloadStatus Status { get; init; }
    public required string FileUrl { get; init; }
    public required string LocalPath { get; init; }
    public string? Filename { get; init; }
    public long? SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public HttpMetadata? Http { get; init; }
    public string? ErrorMessage { get; init; }
}
