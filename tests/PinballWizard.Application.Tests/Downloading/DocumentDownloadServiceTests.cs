using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
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
    public async Task Skips_WhenLocalPathAndSha256AndContentHashSet_WithoutBlobCheck()
    {
        // Fast path: LocalPath, Sha256, AND ContentHash all already set (genuinely
        // fully synced) → skip without calling ExistsAsync.
        var raw = MakeRaw("doc_b", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/y.pdf", Filename = "y.pdf", Sha256 = "abc" },
            contentHash: "abc");
        StubStream(raw);

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(0, summary.Backfilled);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // No blob write and no blob write check needed when fully synced.
        await _blobStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _blobStore.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Cosmos already correct — no redundant self-heal write.
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenSha256Set_ButContentHashMissing_DenormalizesWithoutReadingBlobOrTouchingTimeline()
    {
        // Issue #664, second layer: File.Sha256 was already computed (by a prior
        // download or by the self-heal-from-blob branch), but the top-level
        // ContentHash was never copied from it — the only other writer of
        // ContentHash is UpsertRawAsync (the scraper's re-discovery path), so a
        // document downloaded but not yet re-scraped a second time stays stuck
        // here forever without this branch. No blob read is needed — the hash is
        // already known. Uses DenormalizeContentHashAsync, NOT UpdateFileAsync —
        // no bytes were transferred, so UpdateFileAsync's Timeline.LastDownloadedAt
        // stamp would misrepresent when this document was actually last fetched.
        var raw = MakeRaw("doc_h", "https://sternpinball.com/x/h.pdf",
            file: new DownloadedFileInfo
            {
                LocalPath = "manualspage/h.pdf", Filename = "h.pdf", SizeBytes = 555, Sha256 = "known-hash",
            },
            contentHash: null);
        StubStream(raw);

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(1, summary.Backfilled);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        await _blobStore.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _blobStore.DidNotReceive().TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _blobStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateFileAsync(
            Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).DenormalizeContentHashAsync("doc_h", "known-hash", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenSha256IsEmptyString_TreatsAsUnknownAndFallsThroughToBlobCheck()
    {
        // hasSha256 must use the SAME whitespace check CosmosRawDocumentRepository.
        // UpdateFileAsync uses before copying Sha256 into ContentHash. If this check
        // instead treated Sha256 = "" as "known" (a bare null check), the Tier 2
        // denormalize-only branch would fire every run, call UpdateFileAsync with
        // an empty Sha256, which correctly declines to touch ContentHash (it uses
        // the same whitespace guard) — so hasContentHash would never become true,
        // and backfilled would increment forever without ever fixing the record.
        // An empty Sha256 must instead fall through to the self-heal-from-blob
        // branch, which computes a REAL hash.
        var raw = MakeRaw("doc_empty", "https://sternpinball.com/x/empty.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/empty.pdf", Filename = "empty.pdf", Sha256 = "" },
            contentHash: null);
        StubStream(raw);
        _blobStore.ExistsAsync("manualspage/empty.pdf", Arg.Any<CancellationToken>()).Returns(true);
        _blobStore.GetSizeAsync("manualspage/empty.pdf", Arg.Any<CancellationToken>()).Returns(100L);
        var blobBytes = new byte[] { 1, 2, 3 };
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blobBytes)).ToLowerInvariant();
        _blobStore.TryOpenReadAsync("manualspage/empty.pdf", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(blobBytes)));

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Backfilled);
        await _repo.Received(1).UpdateFileAsync("doc_empty",
            Arg.Is<DownloadedFileInfo>(f => f.Sha256 == expectedSha256),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenLocalPathSet_ButSha256Missing_BackfillsSha256FromExistingBlob()
    {
        // Retroactive repair (issue #664): a record whose LocalPath was stamped by
        // the self-heal branch BEFORE it computed Sha256 (or by any older code path
        // that only wrote LocalPath) is stuck — the OLD fast-path check would skip
        // it forever, since LocalPath alone used to mean "nothing to do". It must
        // fall through to the self-heal branch and backfill Sha256 from the blob
        // that's already there, WITHOUT re-downloading or overwriting the blob.
        var raw = MakeRaw("doc_r", "https://sternpinball.com/x/r.pdf",
            file: new DownloadedFileInfo { LocalPath = "manualspage/r.pdf", Filename = "r.pdf", SizeBytes = 999 });
        StubStream(raw);
        _blobStore.ExistsAsync("manualspage/r.pdf", Arg.Any<CancellationToken>()).Returns(true);
        _blobStore.GetSizeAsync("manualspage/r.pdf", Arg.Any<CancellationToken>()).Returns(999L);
        var blobBytes = new byte[] { 7, 7, 7 };
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blobBytes)).ToLowerInvariant();
        _blobStore.TryOpenReadAsync("manualspage/r.pdf", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(blobBytes)));

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(1, summary.Backfilled);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        await _blobStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateFileAsync("doc_r",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "manualspage/r.pdf" && f.Sha256 == expectedSha256),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenBlobExists_ButBackfillsCosmosRecord_SoItSelfHeals()
    {
        // Durability check: LocalPath not set but blob already in pinwiz-raw
        // (e.g. a prior run wrote the blob but Cosmos update failed). This must
        // NOT be a silent no-op forever — a re-download would always re-hit this
        // same skip check, so the record must be self-healed here (issue #661).
        //
        // Sha256 must ALSO be backfilled from the existing blob (issue #664):
        // once this branch stamps LocalPath, every future run hits the fast-path
        // skip (LocalPath already set) and never revisits this document — so if
        // Sha256 isn't captured here, it stays permanently empty. An empty
        // ContentHash means RAG's rag_index_state short-circuit can never engage
        // for this document, so it gets fully re-embedded on every future
        // --run-rag-backfill run, forever. Reading the already-stored blob to hash
        // it is cheap (our own storage, no external HTTP call) — unlike a real
        // re-download, which this self-heal path exists specifically to avoid.
        var raw = MakeRaw("doc_c", "https://sternpinball.com/x/z.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync("manualspage/z.pdf", Arg.Any<CancellationToken>()).Returns(true);
        _blobStore.GetSizeAsync("manualspage/z.pdf", Arg.Any<CancellationToken>()).Returns(4321L);
        var blobBytes = new byte[] { 10, 20, 30, 40, 50 };
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blobBytes)).ToLowerInvariant();
        _blobStore.TryOpenReadAsync("manualspage/z.pdf", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(blobBytes)));

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(1, summary.Backfilled);
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // No network download, but Cosmos IS stamped from the existing blob.
        await _blobStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateFileAsync("doc_c",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "manualspage/z.pdf"
                && f.Filename == "z.pdf"
                && f.SizeBytes == 4321L
                && f.Sha256 == expectedSha256),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenBlobExists_ButBlobVanishesBeforeHashRead_BackfillsWithNullSha256()
    {
        // TOCTOU: ExistsAsync said true, but the blob is gone by the time we try
        // to read it for hashing (mirrors the existing GetSizeAsync race test).
        // Must not throw — degrade to a null Sha256 rather than abandon the
        // backfill or crash the whole run.
        var raw = MakeRaw("doc_v", "https://sternpinball.com/x/v.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync("manualspage/v.pdf", Arg.Any<CancellationToken>()).Returns(true);
        _blobStore.GetSizeAsync("manualspage/v.pdf", Arg.Any<CancellationToken>()).Returns(100L);
        _blobStore.TryOpenReadAsync("manualspage/v.pdf", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Backfilled);
        await _repo.Received(1).UpdateFileAsync("doc_v",
            Arg.Is<DownloadedFileInfo>(f => f.Sha256 == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenBlobExists_AndSizeLookupMisses_BackfillsWithZeroSize()
    {
        // A race between ExistsAsync and GetSizeAsync (blob deleted in between) must
        // not throw or abandon the backfill — fall back to SizeBytes = 0 rather than
        // leave the record desynced.
        var raw = MakeRaw("doc_g", "https://sternpinball.com/x/w.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync("manualspage/w.pdf", Arg.Any<CancellationToken>()).Returns(true);
        _blobStore.GetSizeAsync("manualspage/w.pdf", Arg.Any<CancellationToken>()).Returns((long?)null);

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.Backfilled);
        await _repo.Received(1).UpdateFileAsync("doc_g",
            Arg.Is<DownloadedFileInfo>(f => f.SizeBytes == 0),
            Arg.Any<CancellationToken>());
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

    // ── TooLarge terminal skip semantics ─────────────────────────────────

    [Fact]
    public async Task TooLarge_FirstEncounter_StampsSkipRecord_CountsAsSkippedTooLarge_NotFailed()
    {
        // A file whose size exceeds the cap on first attempt must be recorded as
        // a terminal skip (not a failure), so future runs don't re-attempt it and
        // it doesn't contribute to the nightly failure count / exit-code-1 alert.
        const long observedBytes = 600L * 1024 * 1024;  // 600 MB > default 500 MB cap
        var raw = MakeRaw("doc_tl1", "https://spookypinball.com/x/S3-v4.0.0.bin", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.TooLarge,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "spookypinball/S3-v4.0.0.bin",
                SizeBytes = observedBytes,
                ErrorMessage = $"{observedBytes:N0} bytes exceeds MaxFileSizeBytes=524288000",
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        // Counted as skippedTooLarge — NOT as failed and NOT as downloaded/skipped.
        Assert.Equal(1, summary.SkippedTooLarge);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Downloaded);

        // Skip record must be stamped in Cosmos with the correct metadata so
        // a future run can decide whether to re-attempt after a cap raise.
        await _repo.Received(1).MarkDownloadSkipAsync(
            "doc_tl1",
            Arg.Is<DownloadSkipInfo>(s =>
                s.Reason == DownloadSkipInfo.Reasons.TooLarge &&
                s.ObservedSizeBytes == observedBytes),
            Arg.Any<CancellationToken>());

        // File record is NOT stamped — the document was never actually downloaded.
        await _repo.DidNotReceive().UpdateFileAsync(
            Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TooLarge_AlreadyStamped_SkipsWithoutCallingDownloader_WhenCapNotRaised()
    {
        // A doc already stamped as TooLarge with an observed size that still
        // exceeds the current cap must be silently skipped — the downloader must
        // never be invoked, and skippedTooLarge (not failed) is incremented.
        const long observedBytes = 600L * 1024 * 1024;   // 600 MB
        const long capAtSkip = 500L * 1024 * 1024;        // 500 MB (default cap, unchanged)
        var raw = MakeRaw("doc_tl2", "https://spookypinball.com/x/S3-v4.0.0.bin", file: null,
            downloadSkip: new DownloadSkipInfo
            {
                Reason = DownloadSkipInfo.Reasons.TooLarge,
                ObservedSizeBytes = observedBytes,
                CapBytesAtSkip = capAtSkip,
                SkippedAt = DateTime.UtcNow.AddDays(-1),
            });
        StubStream(raw);

        // Default cap = 500 MB; observed = 600 MB → 600 ≤ 500 = false → no retry.
        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.SkippedTooLarge);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Downloaded);

        // Downloader must NOT be called — the point is to avoid a futile attempt.
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // No new skip record stamp needed (already stamped).
        await _repo.DidNotReceive().MarkDownloadSkipAsync(
            Arg.Any<string>(), Arg.Any<DownloadSkipInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TooLarge_AlreadyStamped_ReAttempts_WhenCapIsRaisedAboveObservedSize()
    {
        // When the cap has since been raised above the previously-observed file size
        // (ObservedSizeBytes ≤ current MaxFileSizeBytes), the terminal skip must be
        // treated as stale and the download must be re-attempted.
        const long observedBytes = 400L * 1024 * 1024;   // 400 MB (file was measured)
        const long oldCap = 300L * 1024 * 1024;           // 300 MB (cap when skipped)
        // Default ScraperSettings.MaxFileSizeBytes = 500 MB > 400 MB → should retry.
        var raw = MakeRaw("doc_tl3", "https://spookypinball.com/x/S3-v3.0.0.bin", file: null,
            downloadSkip: new DownloadSkipInfo
            {
                Reason = DownloadSkipInfo.Reasons.TooLarge,
                ObservedSizeBytes = observedBytes,
                CapBytesAtSkip = oldCap,
                SkippedAt = DateTime.UtcNow.AddDays(-7),
            });
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "spookypinball/S3-v3.0.0.bin",
                Filename = "S3-v3.0.0.bin",
                SizeBytes = observedBytes,
                Sha256 = "abc123",
                Content = new MemoryStream(),
            });

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        // The cap raise allowed the download to proceed.
        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.SkippedTooLarge);
        Assert.Equal(0, summary.Failed);

        // Downloader WAS invoked — the stale skip was ignored.
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!, Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_BypassesTooLargeTerminalSkip_AndReAttempts()
    {
        // --force-redownload must bypass ALL skip checks including the TooLarge
        // terminal skip so the operator can force a re-attempt (e.g. after raising
        // the cap) without waiting for the stale skip to be re-evaluated.
        var raw = MakeRaw("doc_tl4", "https://spookypinball.com/x/S3-v4.1.0.bin", file: null,
            downloadSkip: new DownloadSkipInfo
            {
                Reason = DownloadSkipInfo.Reasons.TooLarge,
                ObservedSizeBytes = 600L * 1024 * 1024,
                CapBytesAtSkip = 500L * 1024 * 1024,
                SkippedAt = DateTime.UtcNow.AddDays(-1),
            });
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "spookypinball/S3-v4.1.0.bin",
                Filename = "S3-v4.1.0.bin",
                Content = new MemoryStream(),
            });

        var summary = await MakeSvc().RunAsync(force: true, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.SkippedTooLarge);

        // Force must invoke the downloader even though DownloadSkip is stamped.
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    // ── PermanentRejection terminal skip semantics (#839) ────────────────

    [Fact]
    public async Task PermanentRejection_FirstEncounter_StampsSkipRecord_CountsInNewBucket_NotFailed()
    {
        // A file whose download returns a permanent client-side rejection (403/404/410)
        // on first attempt must be recorded as a terminal skip (not a failure), so
        // future runs don't re-attempt it and it doesn't contribute to the nightly
        // failure count / exit-code-1 alert (#839, mirrors TooLarge #819).
        var raw = MakeRaw("doc_7d1e20", "https://spookypinball.s3.us-east-2.amazonaws.com/ultraman/software_versions/v1.08a/code_UM.pkg", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.PermanentRejection,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "spookysupportpage/code_UM.pkg",
                ErrorMessage = "HTTP 403 Forbidden",
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        // Counted in the new SkippedPermanentRejection bucket — NOT as failed.
        Assert.Equal(1, summary.SkippedPermanentRejection);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(0, summary.SkippedTooLarge);

        // Skip record must be stamped in Cosmos so future runs can bypass the downloader.
        await _repo.Received(1).MarkDownloadSkipAsync(
            "doc_7d1e20",
            Arg.Is<DownloadSkipInfo>(s =>
                s.Reason == DownloadSkipInfo.Reasons.PermanentRejection &&
                s.ObservedSizeBytes == null),
            Arg.Any<CancellationToken>());

        // File record is NOT stamped — the document was never actually downloaded.
        await _repo.DidNotReceive().UpdateFileAsync(
            Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentRejection_AlreadyStamped_SkipsWithoutCallingDownloader()
    {
        // A doc already stamped as PermanentRejection must be silently skipped on every
        // subsequent run — the downloader must never be invoked. Unlike TooLarge there is
        // no operator-configurable parameter that might allow a re-attempt; the only
        // recovery path is --force-redownload.
        var raw = MakeRaw("doc_pr_stamped", "https://spookypinball.s3.amazonaws.com/x.pkg", file: null,
            downloadSkip: new DownloadSkipInfo
            {
                Reason = DownloadSkipInfo.Reasons.PermanentRejection,
                ObservedSizeBytes = null,
                CapBytesAtSkip = 0,
                SkippedAt = DateTime.UtcNow.AddDays(-3),
            });
        StubStream(raw);

        var summary = await MakeSvc().RunAsync(force: false, CancellationToken.None);

        Assert.Equal(1, summary.SkippedPermanentRejection);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Downloaded);

        // Downloader must NOT be called — the point is to avoid a futile round-trip.
        await _downloader.DidNotReceive().DownloadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
        // No new skip record stamp needed (already stamped in a prior run).
        await _repo.DidNotReceive().MarkDownloadSkipAsync(
            Arg.Any<string>(), Arg.Any<DownloadSkipInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_BypassesPermanentRejectionTerminalSkip_AndReAttempts()
    {
        // --force-redownload must bypass ALL skip checks including the PermanentRejection
        // terminal skip so the operator can force a re-check (e.g. after the S3 bucket
        // policy or URL has been corrected) without waiting for the stale skip to be cleared.
        var raw = MakeRaw("doc_pr_force", "https://spookypinball.s3.amazonaws.com/y.pkg", file: null,
            downloadSkip: new DownloadSkipInfo
            {
                Reason = DownloadSkipInfo.Reasons.PermanentRejection,
                ObservedSizeBytes = null,
                CapBytesAtSkip = 0,
                SkippedAt = DateTime.UtcNow.AddDays(-1),
            });
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "spookysupportpage/y.pkg",
                Filename = "y.pkg",
                Content = new MemoryStream(),
            });

        var summary = await MakeSvc().RunAsync(force: true, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(0, summary.SkippedPermanentRejection);

        // Force must invoke the downloader even though DownloadSkip is stamped.
        await _downloader.Received(1).DownloadAsync(
            raw.Source.FileUrl!, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailure_StillCountsFailed_AndDoesNotStampSkipRecord()
    {
        // A genuinely unexpected failure (e.g. HTTP 500 Internal Server Error) must
        // NOT be classified as a terminal skip — it still counts as Failed and drives
        // exit code 1. Only the specific permanent client-side rejection codes
        // (403/404/410) receive terminal-skip treatment (#839).
        var raw = MakeRaw("doc_transient", "https://sternpinball.com/broken.pdf", file: null);
        StubStream(raw);
        _blobStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Failed,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/broken.pdf",
                ErrorMessage = "HTTP 500 Internal Server Error",
            });

        var svc = MakeSvc();
        var summary = await svc.RunAsync(force: false, CancellationToken.None);

        // Transient failure: Failed=1, exit-code-driving bucket populated.
        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.SkippedPermanentRejection);
        Assert.Equal(0, summary.SkippedTooLarge);
        Assert.Equal(0, summary.Downloaded);

        // No terminal skip record stamped — a 500 may succeed on the next run.
        await _repo.DidNotReceive().MarkDownloadSkipAsync(
            Arg.Any<string>(), Arg.Any<DownloadSkipInfo>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DocumentDownloadService MakeSvc() =>
        MakeSvc(new ScraperSettings().MaxFileSizeBytes);

    private DocumentDownloadService MakeSvc(long maxFileSizeBytes) =>
        new(_repo, _downloader, _blobStore,
            Options.Create(new ScraperSettings { MaxFileSizeBytes = maxFileSizeBytes }),
            NullLogger<DocumentDownloadService>.Instance);

    private void StubStream(params RawDocumentRecord[] docs) =>
        _repo.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(ToAsync(docs));

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsync(IEnumerable<RawDocumentRecord> docs)
    {
        foreach (var d in docs) { yield return d; await Task.Yield(); }
    }

    private static RawDocumentRecord MakeRaw(
        string documentId, string fileUrl, DownloadedFileInfo? file, HttpMetadata? http = null,
        string? contentHash = null, DownloadSkipInfo? downloadSkip = null) => new()
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
        ContentHash = contentHash,
        DownloadSkip = downloadSkip,
    };
}
