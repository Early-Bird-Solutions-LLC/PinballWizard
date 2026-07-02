using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.P3Sdk;

// Synthesizes RAG chunks from the Multimorphic P3 SDK local zip or extracted
// directory. Indexes high-value developer documents (per-module UsageInstructions
// + INSTALL.txt + ReleaseNotes.txt) as DocumentType.SdkGuide chunks via
// IRagIndexer.
//
// Skips the 1,032 Doxygen HTML files (dense API reference, low narrative value
// for chat RAG). Follows the TWIP/Kineticist synthesis pattern: no Cosmos
// scraped_documents records, no change-feed pipeline. Idempotent: document_id
// is a stable hash of the canonical file path, so re-runs overwrite in place.
//
// Canonical URL convention: https://www.multimorphic.com/sdk/v0.9/{rel-path}
// (forward-slash normalised). The URL is synthetic — Multimorphic does not
// publish the SDK files online — but the host+path uniquely identifies each
// document for the Wizard's citation surface.
public sealed class P3SdkDocsSynthesizer
{
    // Relative paths (from the SDK root, normalised to forward slashes)
    // of the files we want to index. Doxygen HTML under Documentation/ is
    // intentionally excluded — high volume, low narrative value for RAG.
    private static readonly string[] HighValueRelativePaths =
    [
        "INSTALL.txt",
        "ReleaseNotes.txt",
        ".multimorphic/P3/ModuleDrivers/CCR/2.3.1.1/UsageInstructions.txt",
        ".multimorphic/P3/ModuleDrivers/FR/1.0.4.7/UsageInstructions.txt",
        ".multimorphic/P3/ModuleDrivers/Heist/1.2.1.3/UsageInstructions.txt",
        ".multimorphic/P3/ModuleDrivers/Portal/1.0.3.2/UsageInstructions.md",
        ".multimorphic/P3/ModuleDrivers/TPB/1.0.1.1/UsageInstructions.txt",
        ".multimorphic/P3/ModuleDrivers/WAMONH/1.3.0.2/UsageInstructions.txt",
    ];

    // Base URL used as the citation prefix for the Wizard's answer surface.
    // Synthetic: the SDK is a local download, not a public web resource.
    private const string SdkBaseUrl = "https://www.multimorphic.com/sdk/v0.9/";

    private readonly IChunker _chunker;
    private readonly IRagIndexer _ragIndexer;
    private readonly ILogger<P3SdkDocsSynthesizer> _logger;

    public P3SdkDocsSynthesizer(
        IChunker chunker,
        IRagIndexer ragIndexer,
        ILogger<P3SdkDocsSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(ragIndexer);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _ragIndexer = ragIndexer;
        _logger = logger;
    }

    // Indexes P3 SDK high-value docs from sdkPath.
    //
    //   sdkPath: path to either the P3_SDK_V0.9.zip file OR the directory
    //            into which the zip was already extracted.  When a zip is
    //            provided it is extracted to a system temp directory
    //            automatically and cleaned up on completion.
    //
    // Returns the number of files successfully indexed.
    public async Task<int> SyncAsync(string sdkPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkPath);

        string? tempDir = null;
        string rootDir;

        if (File.Exists(sdkPath) && sdkPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            tempDir = Path.Join(Path.GetTempPath(), $"p3sdk_{Guid.NewGuid():N}");
            _logger.LogInformation("P3SdkDocsSynthesizer: extracting zip to {TempDir}.", tempDir);
            ZipFile.ExtractToDirectory(sdkPath, tempDir, overwriteFiles: true);
            // The zip unpacks into a single P3_SDK_V0.9 subdirectory.
            var inner = Directory.GetDirectories(tempDir);
            rootDir = inner.Length == 1 ? inner[0] : tempDir;
        }
        else if (Directory.Exists(sdkPath))
        {
            rootDir = sdkPath;
        }
        else
        {
            throw new ArgumentException(
                $"sdkPath '{sdkPath}' is neither an existing .zip file nor an existing directory.",
                nameof(sdkPath));
        }

        try
        {
            return await IndexHighValueFilesAsync(rootDir, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (tempDir is not null && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
                _logger.LogDebug("P3SdkDocsSynthesizer: cleaned up temp dir {TempDir}.", tempDir);
            }
        }
    }

    private async Task<int> IndexHighValueFilesAsync(string rootDir, CancellationToken cancellationToken)
    {
        var indexed = 0;
        var indexerOptions = new RagIndexerOptions();

        foreach (var relativePath in HighValueRelativePaths)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Normalise to OS path separator for the local filesystem lookup.
            // osRelative is always a trusted, relative segment from the compile-time
            // HighValueRelativePaths list — Path.Join never drops rootDir the way
            // Path.Combine would if a segment were rooted (CodeQL cs/path-combine).
            var osRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Join(rootDir, osRelative);

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning(
                    "P3SdkDocsSynthesizer: expected file not found: {Path}. Skipping.",
                    fullPath);
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning(
                    "P3SdkDocsSynthesizer: file '{RelPath}' is empty; skipping (Invariant #17).",
                    relativePath);
                continue;
            }

            var documentId = BuildDocumentId(relativePath);
            var documentUrl = BuildDocumentUrl(relativePath);
            var title = BuildTitle(relativePath);
            var attributed = BuildAttributedText(title, documentUrl, content);

            var chunkRequest = new ChunkRequest(
                MachineId: IngestionSourceIds.MultimorphicP3Sdk,
                MachineTitle: "Multimorphic P3",
                Manufacturer: "Multimorphic",
                DocumentId: documentId,
                DocumentUrl: documentUrl,
                DocumentType: DocumentType.SdkGuide,
                LastScrapedUtc: DateTimeOffset.UtcNow);

            var extracted = new ExtractedDocument(
                Status: ExtractionStatus.Success,
                Text: attributed,
                Pages: [new ExtractedPage(PageNumber: 1, Text: attributed)],
                Outline: [],
                Error: null);

            var chunks = _chunker.Chunk(extracted, chunkRequest, cancellationToken);
            if (chunks.Count == 0)
            {
                _logger.LogWarning(
                    "P3SdkDocsSynthesizer: no chunks produced for '{RelPath}'; skipping.",
                    relativePath);
                continue;
            }

            var result = await _ragIndexer.UpsertAsync(chunkRequest, chunks, indexerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result.Failures.Count > 0)
            {
                foreach (var failure in result.Failures)
                {
                    _logger.LogError(
                        "P3SdkDocsSynthesizer: AI Search rejected chunk '{ChunkId}' for '{RelPath}': HTTP {StatusCode} — {Error}",
                        failure.ChunkId, relativePath, failure.StatusCode, failure.ErrorMessage);
                }
            }
            else
            {
                _logger.LogInformation(
                    "P3SdkDocsSynthesizer: indexed '{RelPath}' → {Count} chunk(s).",
                    relativePath,
                    chunks.Count);
                indexed++;
            }
        }

        return indexed;
    }

    // Builds a stable document_id by hashing the canonical path.
    // Format: "p3sdk_{sha256_prefix}" (first 16 hex chars of SHA-256).
    // This mirrors the doc_ / mch_ provenance ID convention (ADR-0002).
    private static string BuildDocumentId(string relativePath)
    {
        var canonical = $"p3-sdk://v0.9/{relativePath.Replace('\\', '/')}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
        return $"p3sdk_{Convert.ToHexString(bytes)[..16].ToLowerInvariant()}";
    }

    private static string BuildDocumentUrl(string relativePath) =>
        SdkBaseUrl + relativePath.Replace('\\', '/');

    // Derives a human-readable title from the file's relative path.
    // UsageInstructions files are titled by their module name; top-level
    // files use their base filename (without extension).
    private static string BuildTitle(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        // Pattern: .multimorphic/P3/ModuleDrivers/{Module}/{version}/UsageInstructions.*
        var parts = normalized.Split('/');
        if (parts.Length >= 5 && parts[^1].StartsWith("UsageInstructions", StringComparison.OrdinalIgnoreCase))
        {
            var module = parts[^3]; // e.g. "CCR", "Heist", "Portal"
            return $"P3 Module Driver: {module} — Usage Instructions";
        }

        var baseName = Path.GetFileNameWithoutExtension(relativePath);
        return $"P3 SDK: {baseName}";
    }

    private static string BuildAttributedText(string title, string documentUrl, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: Multimorphic P3 SDK v0.9. Reference: {documentUrl}");
        sb.AppendLine();
        sb.Append(content);
        return sb.ToString();
    }
}
