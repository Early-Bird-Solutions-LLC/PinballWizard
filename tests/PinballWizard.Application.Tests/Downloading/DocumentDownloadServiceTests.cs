using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Downloading;

/// <summary>
/// Tests for <see cref="DocumentDownloadService"/> — politely downloads every
/// not-yet-downloaded raw document so the linker's page-text tiers can read
/// page-1 content. Idempotent: docs with a local file are skipped.
/// </summary>
public sealed class DocumentDownloadServiceTests
{
    private readonly IFileDownloader _downloader = Substitute.For<IFileDownloader>();
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();

    [Fact]
    public async Task Downloads_MissingFile_AndStampsLocalPath()
    {
        var raw = MakeRaw("doc_a", "https://sternpinball.com/x/Godzilla_Pro_web.pdf", file: null);
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/Godzilla_Pro_web.pdf",
                Filename = "Godzilla_Pro_web.pdf",
                SizeBytes = 1234,
                Sha256 = "abc",
            });

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance);
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.Skipped);
        await _repo.Received(1).UpdateFileAsync("doc_a",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "manualspage/Godzilla_Pro_web.pdf"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PassesRelativePath_NotAbsolute_ToDownloader()
    {
        // The IFileDownloader owns the downloads-root and combines it; the service
        // must hand it a path RELATIVE to that root so the persisted LocalPath stays
        // portable across environments (not a machine-absolute path baked into Cosmos).
        var raw = MakeRaw("doc_a", "https://sternpinball.com/x/Godzilla_Pro_web.pdf", file: null);
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/Godzilla_Pro_web.pdf",
                Filename = "Godzilla_Pro_web.pdf",
            });

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance);
        await svc.RunAsync(force: false, CancellationToken.None);

        // {sourceType}/{filename} via Path.Combine (dir separator is platform-specific),
        // and crucially NOT rooted/absolute — Path.IsPathRooted must be false.
        var expectedRelPath = Path.Combine("manualspage", "Godzilla_Pro_web.pdf");
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!,
            Arg.Is<string>(p => p == expectedRelPath && !Path.IsPathRooted(p)),
            Arg.Any<HttpMetadata?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_AlreadyDownloaded()
    {
        var raw = MakeRaw("doc_b", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/y.pdf", Filename = "y.pdf" });
        StubStream(raw);

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance);
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_ReDownloadsAlreadyDownloadedDoc_WithUnconditionalGet()
    {
        // A doc whose Cosmos record already has a LocalPath (downloaded by a prior /
        // ephemeral run) — the actual file is NOT on this machine. Force must (a) skip the
        // already-downloaded skip and re-fetch, and (b) bypass conditional headers (pass
        // null previousMetadata) so the server returns the bytes rather than a 304
        // NotModified that would leave no local file for the linker's page-1 tier to read.
        var raw = MakeRaw("doc_b", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/y.pdf", Filename = "y.pdf" },
            http: new HttpMetadata { ETag = "\"prev-etag\"" });
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/y.pdf",
                Filename = "y.pdf",
            });

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance);
        var summary = await svc.RunAsync(force: true, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.Skipped);
        // Unconditional GET — previousMetadata is null even though the record carries an ETag.
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CountsFailure_WhenDownloadFails()
    {
        var raw = MakeRaw("doc_c", "https://sternpinball.com/x/z.pdf", file: null);
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Failed,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/z.pdf",
                ErrorMessage = "404",
            });

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance);
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PolitenessAbort_PoisonsOrigin_SkipsItsRemainingDocs_ButContinuesOtherOrigins()
    {
        // A politeness abort on one origin must NOT abandon healthy origins: the
        // poisoned origin's remaining docs are skipped, other origins still download,
        // and a summary is still returned (never an exception out of RunAsync).
        var poisoned1 = MakeRaw("doc_p1", "https://sternpinball.com/a/first.pdf", file: null);
        var poisoned2 = MakeRaw("doc_p2", "https://sternpinball.com/a/second.pdf", file: null);
        var healthy = MakeRaw("doc_h", "https://jerseyjackpinball.com/b/ok.pdf", file: null);
        StubStream(poisoned1, poisoned2, healthy);

        _downloader.DownloadAsync(
                "https://sternpinball.com/a/first.pdf", Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.PolitenessAbort,
                FileUrl = "https://sternpinball.com/a/first.pdf",
                LocalPath = "manualspage/first.pdf",
                ErrorMessage = "robots disallow",
            });
        _downloader.DownloadAsync(
                "https://jerseyjackpinball.com/b/ok.pdf", Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = "https://jerseyjackpinball.com/b/ok.pdf",
                LocalPath = "manualspage/ok.pdf",
                Filename = "ok.pdf",
            });

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance);
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        // first.pdf aborted (skipped), second.pdf skipped without a download attempt
        // (origin poisoned), ok.pdf downloaded from the healthy origin.
        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Failed);

        // The poisoned origin's SECOND doc is never attempted...
        await _downloader.DidNotReceive().DownloadAsync(
            "https://sternpinball.com/a/second.pdf", Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // ...but the healthy origin's doc is downloaded and stamped.
        await _repo.Received(1).UpdateFileAsync("doc_h", Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void StubStream(params RawDocumentRecord[] docs) =>
        _repo.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(ToAsync(docs));

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsync(IEnumerable<RawDocumentRecord> docs)
    {
        foreach (var d in docs) { yield return d; await Task.Yield(); }
    }

    private static RawDocumentRecord MakeRaw(
        string documentId, string fileUrl, DownloadedFileInfo? file, HttpMetadata? http = null) => new()
    {
        DocumentId = documentId,
        DocumentUrl = fileUrl,
        DocumentType = DocumentType.Manual,
        Source = new SourceInfo
        {
            DiscoveryUrl = "https://sternpinball.com/manuals/",
            DiscoveryContext = "Manuals page",
            FileUrl = fileUrl,
            ScrapedAt = DateTime.UtcNow,
            SourceType = SourceType.ManualsPage,
        },
        Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
        File = file,
        Http = http,
    };
}
