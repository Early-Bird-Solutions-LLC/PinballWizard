namespace PinballWizard.Application.Downloading;

/// <summary>
/// Filesystem operations the <see cref="DownloadPathMigrationService"/> needs,
/// behind an interface so the migration's verify→move→rewrite orchestration is
/// unit-testable without touching disk. Infrastructure provides the real
/// implementation over <c>System.IO</c>.
/// </summary>
public interface IDownloadFileStore
{
    /// <summary>
    /// Computes the SHA-256 (lowercase hex) of the file at <paramref name="path"/>,
    /// or <c>null</c> when the file does not exist. Used to prove the on-disk
    /// bytes match the recorded hash before the migration trusts (and moves) them.
    /// </summary>
    Task<string?> GetSha256Async(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the file from <paramref name="sourcePath"/> to <paramref name="destinationPath"/>,
    /// creating the destination directory if needed. Idempotent: if the source is
    /// already absent but the destination exists, treats the move as already done.
    /// </summary>
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}
