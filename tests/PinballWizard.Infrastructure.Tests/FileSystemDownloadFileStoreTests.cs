using System.Security.Cryptography;
using System.Text;
using PinballWizard.Infrastructure.Downloading;
using Xunit;

namespace PinballWizard.Infrastructure.Tests;

/// <summary>
/// Defends the real-filesystem behavior of <see cref="FileSystemDownloadFileStore"/>:
/// SHA-256 of an existing file, null for an absent file, move-with-dir-creation, and
/// the idempotent "source already moved" recovery branch.
/// </summary>
public sealed class FileSystemDownloadFileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemDownloadFileStore _store = new();

    public FileSystemDownloadFileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pw-filestore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task GetSha256Async_ExistingFile_ReturnsLowercaseHexHash()
    {
        var path = Path.Combine(_tempDir, "a.pdf");
        var bytes = Encoding.UTF8.GetBytes("hello pinball");
        await File.WriteAllBytesAsync(path, bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(expected, await _store.GetSha256Async(path, CancellationToken.None));
    }

    [Fact]
    public async Task GetSha256Async_AbsentFile_ReturnsNull()
    {
        Assert.Null(await _store.GetSha256Async(Path.Combine(_tempDir, "nope.pdf"), CancellationToken.None));
    }

    [Fact]
    public async Task MoveAsync_CreatesDestinationDirectory_AndMovesFile()
    {
        var src = Path.Combine(_tempDir, "src.pdf");
        await File.WriteAllTextAsync(src, "x");
        var dest = Path.Combine(_tempDir, "sub", "dir", "dest.pdf"); // dir does not exist yet

        await _store.MoveAsync(src, dest, CancellationToken.None);

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task MoveAsync_SourceAbsentButDestinationExists_IsNoOp_NotThrow()
    {
        // Interrupted-prior-run residue: source already moved. Treat as done.
        var dest = Path.Combine(_tempDir, "dest.pdf");
        await File.WriteAllTextAsync(dest, "already here");

        await _store.MoveAsync(Path.Combine(_tempDir, "src-gone.pdf"), dest, CancellationToken.None);

        Assert.Equal("already here", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task MoveAsync_SourceAndDestinationBothAbsent_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _store.MoveAsync(
                Path.Combine(_tempDir, "src-gone.pdf"),
                Path.Combine(_tempDir, "dest-gone.pdf"),
                CancellationToken.None));
    }
}
