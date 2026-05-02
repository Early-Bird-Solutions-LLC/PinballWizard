using PinballWizard.Core.Models;

namespace PinballWizard.Application.Downloading;

/// <summary>
/// Contract for downloading documents from a manufacturer or third-party site.
/// The Application layer drives the download loop; Infrastructure provides the
/// actual HTTP + filesystem implementation. Defining the interface here keeps
/// Application free of HTTP / disk concerns and lets each manufacturer scraper
/// reuse the same download contract regardless of which platform it runs on.
/// </summary>
public interface IFileDownloader
{
    /// <summary>
    /// Downloads a file if it has changed since the last download (using
    /// <see cref="HttpMetadata.ETag"/> / <see cref="HttpMetadata.LastModified"/>).
    /// Transient-failure retry is owned by the resilience pipeline registered
    /// on the implementation's HttpClient — by the time this returns, retries
    /// (if any) are already exhausted.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        string fileUrl,
        string localPath,
        HttpMetadata? previousMetadata = null,
        CancellationToken cancellationToken = default);
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
