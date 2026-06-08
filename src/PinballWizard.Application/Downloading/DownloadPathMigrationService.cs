using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Downloading;

/// <summary>
/// One-shot, idempotent, byte-safe migration that corrects legacy already-rooted
/// <c>file.local_path</c> values in <c>scraped_documents_raw</c>.
/// <para>
/// The pre-fix downloader (corrected in the AB#259 edition-scope work) persisted a
/// path that already included the downloads root (e.g.
/// <c>data/downloads/manualspage/x.pdf</c>) AND wrote the file to a doubled
/// location (<c>{root}/{rooted-path}</c>). The contract (see
/// <see cref="DownloadedFileInfo.LocalPath"/>) is that <c>local_path</c> is RELATIVE
/// to the downloads root. This service converges existing rows to that contract:
/// for each rooted row it verifies the on-disk file's SHA-256 matches the recorded
/// hash (refusing to migrate a mismatch — never bless wrong bytes), moves the file
/// from the doubled location to the correct single location, and rewrites
/// <c>local_path</c> to the clean relative form.
/// </para>
/// Idempotent: rows already relative are skipped, so a re-run is a no-op.
/// </summary>
public sealed class DownloadPathMigrationService
{
    private readonly IRawDocumentRepository _repo;
    private readonly IDownloadFileStore _store;
    private readonly ILogger<DownloadPathMigrationService> _logger;
    private readonly string _downloadsRoot;

    public DownloadPathMigrationService(
        IRawDocumentRepository repo,
        IDownloadFileStore store,
        ILogger<DownloadPathMigrationService> logger,
        string downloadsRoot)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(downloadsRoot);
        _repo = repo;
        _store = store;
        _logger = logger;
        _downloadsRoot = downloadsRoot;
    }

    /// <summary>
    /// Strips a leading downloads-root prefix from a stored <c>local_path</c>,
    /// returning the path RELATIVE to the root. Normalizes backslashes to '/'.
    /// A path that is already relative (no root prefix) is returned unchanged, so
    /// the migration is idempotent.
    /// </summary>
    public static string NormalizeToRelative(string storedLocalPath, string downloadsRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(storedLocalPath);
        ArgumentException.ThrowIfNullOrEmpty(downloadsRoot);

        var stored = storedLocalPath.Replace('\\', '/').TrimStart('/');
        var root = downloadsRoot.Replace('\\', '/').Trim('/');
        var prefix = root + "/";

        return stored.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? stored[prefix.Length..]
            : stored;
    }

    // Join with '/' regardless of OS — the on-disk path strings the migration
    // computes must be deterministic across platforms (and match what the store
    // and tests expect). The store implementation normalizes for the real FS.
    private static string CombineForward(string root, string rest) =>
        $"{root.Replace('\\', '/').TrimEnd('/')}/{rest.Replace('\\', '/').TrimStart('/')}";

    public async Task<MigrationSummary> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        int migrated = 0, migratedUnverified = 0, skipped = 0, shaMismatch = 0, missing = 0;

        await foreach (var raw in _repo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = raw.File;
            if (file?.LocalPath is null) { skipped++; continue; }

            var relative = NormalizeToRelative(file.LocalPath, _downloadsRoot);
            if (string.Equals(relative, file.LocalPath.Replace('\\', '/'), StringComparison.Ordinal))
            {
                // Already relative — nothing to migrate.
                skipped++;
                continue;
            }

            // The on-disk file currently sits where the linker would read it:
            // {root}/{storedLocalPath} — i.e. the doubled location. Normalize
            // separators to '/' so the path is stable across OSes (the store
            // implementation accepts either separator).
            var currentOnDisk = CombineForward(_downloadsRoot, file.LocalPath);
            var correctOnDisk = CombineForward(_downloadsRoot, relative);

            // Byte-safety: prove the on-disk bytes are the recorded content before
            // we move them and rewrite the row. Never silently migrate wrong bytes.
            var actualSha = await _store.GetSha256Async(currentOnDisk, cancellationToken).ConfigureAwait(false);
            var alreadyMoved = false;
            if (actualSha is null)
            {
                // The old (doubled) location is empty. Either the file was never
                // downloaded, OR a prior interrupted run already moved it and only
                // the Cosmos rewrite failed. Distinguish by probing the destination:
                // if the file is already correctly placed, this is a recoverable
                // half-done migration — retry just the rewrite. (move-then-rewrite
                // ordering makes this the expected crash residue.)
                var destSha = await _store.GetSha256Async(correctOnDisk, cancellationToken).ConfigureAwait(false);
                if (destSha is null)
                {
                    missing++;
                    _logger.LogWarning("PathMigration: {DocId} file missing at {Path} (and not at destination) — not migrated.", raw.DocumentId, currentOnDisk);
                    continue;
                }
                alreadyMoved = true;
                actualSha = destSha;
            }

            if (!string.IsNullOrEmpty(file.Sha256) && !string.Equals(actualSha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                shaMismatch++;
                _logger.LogWarning(
                    "PathMigration: {DocId} SHA mismatch (recorded={Recorded}, actual={Actual}) — not migrated.",
                    raw.DocumentId, file.Sha256, actualSha);
                continue;
            }

            // A row with no recorded hash can't be byte-verified; it still migrates
            // (the file exists) but is counted separately so the operator sees it
            // was not SHA-proven — honest visibility, never a silent unverified blessing.
            var unverified = string.IsNullOrEmpty(file.Sha256);

            if (!dryRun)
            {
                if (!alreadyMoved)
                {
                    await _store.MoveAsync(currentOnDisk, correctOnDisk, cancellationToken).ConfigureAwait(false);
                }
                // Change ONLY local_path; carry every other field forward verbatim.
                // Copying each field (rather than `new { LocalPath = ... }`) preserves
                // provenance — notably PageCount, which the linker's page tiers + the
                // chunker depend on. Listing all fields here keeps this exhaustive; a
                // field added to DownloadedFileInfo must be added here too.
                await _repo.UpdateFileAsync(raw.DocumentId, new DownloadedFileInfo
                {
                    LocalPath = relative,
                    Filename = file.Filename,
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256,
                    MimeType = file.MimeType,
                    PageCount = file.PageCount,
                }, cancellationToken).ConfigureAwait(false);
            }
            migrated++;
            if (unverified) { migratedUnverified++; }
        }

        _logger.LogInformation(
            "PathMigration {Mode} complete: migrated={Migrated} (unverified={Unverified}) skipped={Skipped} shaMismatch={Mismatch} missing={Missing}",
            dryRun ? "(dry-run)" : "(apply)", migrated, migratedUnverified, skipped, shaMismatch, missing);
        return new MigrationSummary(migrated, skipped, shaMismatch, missing, migratedUnverified);
    }
}

/// <summary>
/// Result counts from a <see cref="DownloadPathMigrationService"/> run.
/// <paramref name="MigratedUnverified"/> is the subset of <paramref name="Migrated"/>
/// that had no recorded SHA-256 to verify against (migrated on file-existence alone).
/// </summary>
public sealed record MigrationSummary(int Migrated, int Skipped, int ShaMismatch, int Missing, int MigratedUnverified = 0);
