using System.Security.Cryptography;
using PinballWizard.Application.Downloading;

namespace PinballWizard.Infrastructure.Downloading;

/// <summary>
/// <see cref="IDownloadFileStore"/> over the local filesystem. Used by the
/// <see cref="DownloadPathMigrationService"/> to verify (SHA-256) and relocate
/// downloaded files on disk. Pure IO; no politeness/network concerns.
/// </summary>
public sealed class FileSystemDownloadFileStore : IDownloadFileStore
{
    public async Task<string?> GetSha256Async(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        // Idempotent: if the source is already gone but the destination exists,
        // a prior (interrupted) run already moved this file — treat as done.
        if (!File.Exists(sourcePath))
        {
            if (File.Exists(destinationPath))
            {
                return Task.CompletedTask;
            }
            throw new FileNotFoundException($"Source file not found for move: {sourcePath}", sourcePath);
        }

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // overwrite:false — a collision means an unexpected duplicate; fail loud
        // rather than silently clobber (the migration pre-checks zero collisions).
        File.Move(sourcePath, destinationPath, overwrite: false);
        return Task.CompletedTask;
    }
}
