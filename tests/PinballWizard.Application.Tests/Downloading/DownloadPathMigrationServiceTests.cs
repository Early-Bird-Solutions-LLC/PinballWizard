using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Downloading;

/// <summary>
/// Tests for <see cref="DownloadPathMigrationService"/> — a one-shot, idempotent,
/// byte-safe migration that corrects legacy already-rooted <c>file.local_path</c>
/// values (e.g. <c>data/downloads/manualspage/x.pdf</c>, written by the pre-fix
/// downloader) to the clean relative form the contract requires
/// (<c>manualspage/x.pdf</c>), moving the on-disk file to the matching single
/// location only after verifying its SHA-256 matches the recorded hash.
/// </summary>
public sealed class DownloadPathMigrationServiceTests
{
    private const string Root = "data/downloads";

    // ── Pure path normalization (the load-bearing logic) ─────────────────

    [Theory]
    // Already-rooted (legacy bug) → stripped to relative.
    [InlineData("data/downloads/manualspage/Godzilla_Pro_web.pdf", "manualspage/Godzilla_Pro_web.pdf")]
    [InlineData("data\\downloads\\manualspage\\Godzilla_Pro_web.pdf", "manualspage/Godzilla_Pro_web.pdf")]
    // Already relative → unchanged (idempotent).
    [InlineData("manualspage/Godzilla_Pro_web.pdf", "manualspage/Godzilla_Pro_web.pdf")]
    [InlineData("gamepage/x.jpg", "gamepage/x.jpg")]
    public void NormalizeToRelative_StripsRootPrefix_Idempotently(string stored, string expected)
    {
        Assert.Equal(expected, DownloadPathMigrationService.NormalizeToRelative(stored, Root));
    }

    [Fact]
    public void NormalizeToRelative_AlreadyRelative_IsUnchanged_SoMigrationIsIdempotent()
    {
        var already = "manualspage/Godzilla_Pro_web.pdf";
        Assert.Equal(already, DownloadPathMigrationService.NormalizeToRelative(already, Root));
    }

    // ── Service orchestration (verify → move → rewrite) ──────────────────

    [Fact]
    public async Task RunAsync_RootedPath_ShaMatches_MovesFileAndRewritesToRelative()
    {
        var raw = MakeRaw("doc_a", localPath: "data/downloads/manualspage/x.pdf", sha: "abc123");
        StubStream(raw);
        // On-disk file is at the OLD (rooted-relative-to-root => doubled) location and its SHA matches.
        _store.GetSha256Async("data/downloads/data/downloads/manualspage/x.pdf", Arg.Any<CancellationToken>())
            .Returns("abc123");

        var summary = await NewService().RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.ShaMismatch);
        // File moved from old doubled location to the correct single location.
        await _store.Received(1).MoveAsync(
            "data/downloads/data/downloads/manualspage/x.pdf",
            "data/downloads/manualspage/x.pdf",
            Arg.Any<CancellationToken>());
        // Cosmos local_path rewritten to clean relative.
        await _repo.Received(1).UpdateFileAsync("doc_a",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "manualspage/x.pdf" && f.Sha256 == "abc123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AlreadyRelativePath_SkipsWithoutMoveOrWrite()
    {
        var raw = MakeRaw("doc_b", localPath: "manualspage/x.pdf", sha: "abc123");
        StubStream(raw);

        var summary = await NewService().RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Equal(1, summary.Skipped);
        await _store.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ShaMismatch_DoesNotMoveOrRewrite_AndCountsMismatch()
    {
        // The on-disk bytes do NOT match the recorded hash — migrating would
        // bless corrupt/wrong content. Must refuse to move or rewrite that row.
        var raw = MakeRaw("doc_c", localPath: "data/downloads/manualspage/x.pdf", sha: "expected_hash");
        StubStream(raw);
        _store.GetSha256Async(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("DIFFERENT_hash");

        var summary = await NewService().RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, summary.ShaMismatch);
        Assert.Equal(0, summary.Migrated);
        await _store.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_MissingOnDiskFile_CountsMissing_DoesNotRewrite()
    {
        var raw = MakeRaw("doc_d", localPath: "data/downloads/manualspage/x.pdf", sha: "abc123");
        StubStream(raw);
        _store.GetSha256Async(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null); // file absent

        var summary = await NewService().RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, summary.Missing);
        Assert.Equal(0, summary.Migrated);
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_DryRun_VerifiesShaButPerformsNoMoveOrWrite()
    {
        var raw = MakeRaw("doc_e", localPath: "data/downloads/manualspage/x.pdf", sha: "abc123");
        StubStream(raw);
        _store.GetSha256Async(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var summary = await NewService().RunAsync(dryRun: true, CancellationToken.None);

        // Dry-run reports what WOULD migrate (1) but performs no side effects.
        Assert.Equal(1, summary.Migrated);
        await _store.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateFileAsync(Arg.Any<string>(), Arg.Any<DownloadedFileInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoFileOnRecord_SkipsSilently()
    {
        var raw = MakeRaw("doc_f", localPath: null, sha: null);
        StubStream(raw);

        var summary = await NewService().RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Migrated);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();
    private readonly IDownloadFileStore _store = Substitute.For<IDownloadFileStore>();

    private DownloadPathMigrationService NewService() =>
        new(_repo, _store, NullLogger<DownloadPathMigrationService>.Instance, downloadsRoot: Root);

    private void StubStream(params RawDocumentRecord[] docs) =>
        _repo.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(ToAsync(docs));

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsync(IEnumerable<RawDocumentRecord> docs)
    {
        foreach (var d in docs) { yield return d; await Task.Yield(); }
    }

    private static RawDocumentRecord MakeRaw(string documentId, string? localPath, string? sha) => new()
    {
        DocumentId = documentId,
        DocumentUrl = "https://sternpinball.com/x.pdf",
        DocumentType = DocumentType.Manual,
        Source = new SourceInfo
        {
            DiscoveryUrl = "https://sternpinball.com/manuals/",
            DiscoveryContext = "Manuals page",
            FileUrl = "https://sternpinball.com/x.pdf",
            ScrapedAt = DateTime.UtcNow,
            SourceType = SourceType.ManualsPage,
        },
        Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
        File = localPath is null ? null : new DownloadedFileInfo
        {
            LocalPath = localPath,
            Filename = System.IO.Path.GetFileName(localPath),
            SizeBytes = 123,
            Sha256 = sha,
        },
    };
}
