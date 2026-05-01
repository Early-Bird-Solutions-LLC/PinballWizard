using System.Globalization;
using System.Net;
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
    /// <summary>Upper bound on backoff delay between retry attempts (30 seconds).</summary>
    private const int MaxBackoffMs = 30_000;

    /// <summary>Upper bound (seconds) we will honor from a Retry-After header before clamping.</summary>
    private const int MaxRetryAfterSeconds = 60;

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
    /// Returns the download result with file metadata. Transient HTTP failures (5xx, 408, 429,
    /// HttpRequestException, HttpClient timeouts) are retried with exponential backoff up to
    /// <see cref="ScraperSettings.MaxRetries"/> times.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string fileUrl,
        string localPath,
        HttpMetadata? previousMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = Path.Combine(_settings.DownloadsPath, localPath);
        var totalAttempts = Math.Max(1, _settings.MaxRetries + 1);
        string? lastErrorMessage = null;

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
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

                response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // 304 Not Modified — file hasn't changed (never retried)
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

                // Decide whether a non-success status is worth retrying before we throw.
                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatus(response.StatusCode) && attempt < totalAttempts - 1)
                    {
                        var statusErr = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                        lastErrorMessage = statusErr;
                        var delay = ComputeDelay(attempt, response);
                        _logger.LogWarning(
                            "Transient HTTP {Status} on attempt {Attempt}/{Total} for {Url}; retrying in {DelayMs}ms",
                            (int)response.StatusCode, attempt + 1, totalAttempts, fileUrl, delay);
                        response.Dispose();
                        response = null;
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }

                    // Non-retryable status, or we're out of attempts — surface as Failed
                    // directly. We can't EnsureSuccessStatusCode() here because the resulting
                    // HttpRequestException would be caught by the retryable-exception handler
                    // below and trigger a retry loop on a permanent client error like 404.
                    var permanentErr = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                    _logger.LogError("Non-retryable HTTP {Status} for {Url}", (int)response.StatusCode, fileUrl);
                    return new DownloadResult
                    {
                        Status = DownloadStatus.Failed,
                        FileUrl = fileUrl,
                        LocalPath = localPath,
                        ErrorMessage = permanentErr
                    };
                }

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
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken
                                                          && cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled — propagate without converting to a Failed result.
                throw;
            }
            catch (Exception ex) when (IsRetryableException(ex) && attempt < totalAttempts - 1)
            {
                lastErrorMessage = ex.Message;
                var delay = ComputeDelay(attempt, response: null);
                _logger.LogWarning(ex,
                    "Transient error on attempt {Attempt}/{Total} for {Url}; retrying in {DelayMs}ms: {Message}",
                    attempt + 1, totalAttempts, fileUrl, delay, ex.Message);
                await Task.Delay(delay, cancellationToken);
                continue;
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
            finally
            {
                response?.Dispose();
            }
        }

        // Exhausted all retries on a retryable status with no exception thrown.
        var finalMessage = lastErrorMessage ?? "Exceeded max retry attempts";
        _logger.LogError("Failed to download after {Total} attempts: {Url} ({Message})",
            totalAttempts, fileUrl, finalMessage);
        return new DownloadResult
        {
            Status = DownloadStatus.Failed,
            FileUrl = fileUrl,
            LocalPath = localPath,
            ErrorMessage = finalMessage
        };
    }

    private static bool IsRetryableStatus(HttpStatusCode status)
    {
        var code = (int)status;
        if (code >= 500 && code <= 599) return true;
        return status == HttpStatusCode.RequestTimeout       // 408
            || status == (HttpStatusCode)429;                 // TooManyRequests (named in newer frameworks)
    }

    private static bool IsRetryableException(Exception ex)
    {
        // HttpClient surfaces request timeouts as TaskCanceledException with no caller token.
        // OperationCanceledException tied to the caller's token is handled separately above.
        return ex is HttpRequestException
            || ex is TaskCanceledException;
    }

    private int ComputeDelay(int attemptIndex, HttpResponseMessage? response)
    {
        // Honor Retry-After when present on 429/503-style responses.
        if (response is not null && TryGetRetryAfterMs(response, out var retryAfterMs))
        {
            return Math.Min(retryAfterMs, MaxRetryAfterSeconds * 1000);
        }

        // Exponential backoff: InitialRetryDelayMs * 2^attemptIndex, capped at MaxBackoffMs.
        var initial = Math.Max(0, _settings.InitialRetryDelayMs);
        // Use long math to avoid overflow on big attempt indices, then clamp.
        var raw = (long)initial << attemptIndex;
        if (raw > MaxBackoffMs) raw = MaxBackoffMs;
        return (int)raw;
    }

    private static bool TryGetRetryAfterMs(HttpResponseMessage response, out int delayMs)
    {
        delayMs = 0;
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return false;

        if (retryAfter.Delta.HasValue)
        {
            var ms = (long)retryAfter.Delta.Value.TotalMilliseconds;
            if (ms < 0) ms = 0;
            delayMs = (int)Math.Min(ms, int.MaxValue);
            return true;
        }

        if (retryAfter.Date.HasValue)
        {
            var ms = (long)(retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (ms < 0) ms = 0;
            delayMs = (int)Math.Min(ms, int.MaxValue);
            return true;
        }

        // Some servers stuff a bare number into Retry-After that doesn't parse as Delta or Date —
        // try one last raw header parse as integer seconds.
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                {
                    if (seconds < 0) seconds = 0;
                    delayMs = seconds * 1000;
                    return true;
                }
            }
        }

        return false;
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
