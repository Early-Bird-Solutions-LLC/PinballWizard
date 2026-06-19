using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Downloading;

/// <summary>
/// Tests for <see cref="DocumentDownloadService"/> — politely downloads every
/// not-yet-stored raw document to the durable pinwiz-raw blob store so content
/// survives across ACA runs. Idempotent: docs already in the blob store
/// (or whose record has a LocalPath) are skipped.
/// </summary>
public sealed class DocumentDownloadServiceTests
{
    private readonly IFileDownloader _downloader = Substitute.For<IFileDownloader>();
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();
    private readonly IDocumentBlobStore _blobStore = Substitute.For<IDocumentBlobStore>();

    // ── Core blob-write behavior ──────────────────────────────────────────

    [Fact]
    public async Task Downloads_MissingFile_WritesBlobAndStampsLocalPath()
    {
        // Not yet stored: record has no LocalPath and ExistsAsync returns false.
        var raw = MakeRaw("doc_a", "https://sternpinball.com/x/Godzilla_Pro_web.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/Godzilla_Pro_web.pdf",
                Filename = "Godzilla_Pro_web.pdf",
                SizeBytes = 1234,
                Sha256 = "abc",
                Content = content,
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.Skipped);

        // Blob written with the relative path as blob name.
        await _blobStore.Received(1).WriteAsync(
            "manualspage/Godzilla_Pro_web.pdf",
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());

        // File record stamped with blob name as LocalPath.
        await _repo.Received(1).UpdateFileAsync("doc_a",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "manualspage/Godzilla_Pro_web.pdf"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PassesRelativePath_AsBlobName_ToDownloaderAndBlobStore()
    {
        // The blob name passed to both IFileDownloader and IDocumentBlobStore must be
        // the {sourceType}/{filename} relative path (forward-slash, no absolute root).
        var raw = MakeRaw("doc_a", "https://sternpinball.com/x/Godzilla_Pro_web.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/Godzilla_Pro_web.pdf",
                Filename = "Godzilla_Pro_web.pdf",
                Content = new MemoryStream(),
            });

        await MakeSvc().RunAsync(force: false, CancellationToken.None);

        // Blob name is {sourceType}/{filename} with forward-slash (not OS path separator).
        const string expectedBlobName = "manualspage/Godzilla_Pro_web.pdf";
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!,
            Arg.Is<string>(p => p == expectedBlobName && !Path.IsPathRooted(p)),
            Arg.Any<HttpMetadata?>(),
            Arg.Any<CancellationToken>());
        await _blobStore.Received(1).WriteAsync(
            Arg.Is<string>(p => p == expectedBlobName),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    // ── Incremental skip (the idempotency contract) ───────────────────────

    [Fact]
    public async Task Skips_WhenLocalPathSet_WithoutBlobCheck()
    {
        // Fast path: LocalPath is already set → skip without calling ExistsAsync.
        var raw = MakeRaw("doc_b", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/y.pdf", Filename = "y.pdf" });
        StubStream(raw);

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // No blob write and no blob write check needed when LocalPath is already set.
        await _blobStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenBlobExists_EvenWithoutLocalPath()
    {
        // Durability check: LocalPath not set but blob already in pinwiz-raw
        // (e.g. a prior run wrote the blob but Cosmos update failed) → skip.
        var raw = MakeRaw("doc_c", "https://sternpinball.com/x/z.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync("manualspage/z.pdf", Arg.Any<CancellationToken>()).Returns(true);

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
    }

    // ── Force (--force-redownload) semantics ─────────────────────────────

    [Fact]
    public async Task Force_ReDownloads_AndOverwritesBlob_EvenWhenAlreadyStored()
    {
        // A doc whose Cosmos record already has a LocalPath and whose blob exists.
        // Force must (a) skip the already-stored check and re-fetch, (b) bypass
        // conditional headers (pass null previousMetadata) so the server returns
        // the bytes rather than a 304 NotModified, and (c) overwrite the blob.
        var raw = MakeRaw("doc_d", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/y.pdf", Filename = "y.pdf" },
            http: new HttpMetadata { ETag = "\"prev-etag\"" });
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/y.pdf",
                Filename = "y.pdf",
                Content = new MemoryStream(),
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: true, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.Skipped);

        // Unconditional GET — previousMetadata is null even though the record carries an ETag.
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!, Arg.Any<string>(), null, Arg.Any<CancellationToken>());

        // Blob overwritten (WriteAsync called with overwrite semantics in BlobDocumentStore).
        await _blobStore.Received(1).WriteAsync(
            "manualspage/y.pdf", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_DoesNotCallExistsAsync_OnForcedRun()
    {
        // Force bypasses ALL skip checks — ExistsAsync must NOT be called when force is true,
        // because the whole point of force is to ignore the "already stored" state.
        var raw = MakeRaw("doc_e", "https://sternpinball.com/x/y.pdf", file: null);
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/y.pdf",
                Filename = "y.pdf",
                Content = new MemoryStream(),
            });

        await MakeSvc().RunAsync(force: true, CancellationToken.None);

        await _blobStore.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Existing behavior preserved ───────────────────────────────────────

    [Fact]
    public async Task CountsFailure_WhenDownloadFails_AndDoesNotWriteBlob()
    {
        var raw = MakeRaw("doc_f", "https://sternpinball.com/x/z.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Failed,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/z.pdf",
                ErrorMessage = "404",
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        // No blob write and no Cosmos stamp on failure.
        await _blobStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
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
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

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
                Content = new MemoryStream(),
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        // first.pdf aborted (skipped), second.pdf skipped without a download attempt
        // (origin poisoned), ok.pdf downloaded from the healthy origin.
        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Failed);

        // The poisoned origin's SECOND doc is never attempted...
        await _downloader.DidNotReceive().DownloadAsync(
            "https://sternpinball.com/a/second.pdf", Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // ...but the healthy origin's doc is downloaded, written to blob, and stamped.
        await _blobStore.Received(1).WriteAsync("manualspage/ok.pdf", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateFileAsync("doc_h", Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DocumentDownloadService MakeSvc() =>
        new(_repo, _downloader, _blobStore, NullLogger<DocumentDownloadService>.Instance);

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
