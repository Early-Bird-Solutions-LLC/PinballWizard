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
    Failed,

    /// <summary>
    /// The download was refused by the politeness gate (robots.txt disallow, or a
    /// 429 streak that exceeded the configured maximum) — a deliberate "stop asking
    /// this origin" signal, distinct from a per-file <see cref="Failed"/>. The caller
    /// should stop downloading from this origin but may continue with other origins.
    /// </summary>
    PolitenessAbort,

    /// <summary>
    /// The server returned a permanent client-side rejection: 403 Forbidden, 404 Not
    /// Found, or 410 Gone. These indicate the resource is structurally unavailable at
    /// this URL — not a transient network error that retry would fix. The caller stamps
    /// a terminal skip record so future runs bypass the download attempt rather than
    /// re-hitting a known-dead URL every night (mirrors the <see cref="TooLarge"/>
    /// pattern, #819/#839).
    /// </summary>
    PermanentRejection
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

    /// <summary>
    /// The downloaded bytes as a readable stream. Set only when
    /// <see cref="Status"/> is <see cref="DownloadStatus.Downloaded"/>;
    /// null for all other statuses (NotModified, Failed, etc.).
    /// The caller is responsible for disposing this stream.
    /// </summary>
    public Stream? Content { get; init; }
}
