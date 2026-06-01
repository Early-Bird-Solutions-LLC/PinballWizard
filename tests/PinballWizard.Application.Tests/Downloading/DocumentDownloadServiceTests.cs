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

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance, downloadsRoot: "/tmp/dl");
        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.Skipped);
        await _repo.Received(1).UpdateFileAsync("doc_a",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "manualspage/Godzilla_Pro_web.pdf"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_AlreadyDownloaded()
    {
        var raw = MakeRaw("doc_b", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/y.pdf", Filename = "y.pdf" });
        StubStream(raw);

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance, downloadsRoot: "/tmp/dl");
        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
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

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance, downloadsRoot: "/tmp/dl");
        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void StubStream(params RawDocumentRecord[] docs) =>
        _repo.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(ToAsync(docs));

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsync(IEnumerable<RawDocumentRecord> docs)
    {
        foreach (var d in docs) { yield return d; await Task.Yield(); }
    }

    private static RawDocumentRecord MakeRaw(string documentId, string fileUrl, DownloadedFileInfo? file) => new()
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
    };
}
