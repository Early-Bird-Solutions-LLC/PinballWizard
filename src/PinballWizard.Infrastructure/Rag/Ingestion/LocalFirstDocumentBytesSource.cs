using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// IDocumentBytesSource decorator that serves bytes from the local downloads tree
// when the document's file is present, delegating to the inner (HTTP) source only
// when it is absent.
//
// WHY: a full RAG backfill (--run-rag-backfill) re-ingests every scraped_document.
// The inner HttpDocumentBytesSource re-fetches each PDF from its source URL, which
// — for sources behind the politeness gate's per-origin throttle (e.g. Stern at
// 10s) — turns a backfill into hours of mostly-idle waiting AND re-hammers source
// sites for bytes already on disk. The download pass (--download-documents /
// --migrate-download-paths) has already placed (and SHA-verified) those files
// locally under {downloadsRoot}/{sourceType}/{filename}. Reading them is faster
// and politer (zero source traffic for already-downloaded docs).
//
// Lookup is by filename: the corpus's filenames are globally unique across the
// per-source-type subdirs, so a single recursive filename index resolves the URL's
// basename to a local path. The index is built once, lazily, and reused for the
// life of the (singleton) source.
//
// Absent-file fallback is the steady-state Change-Feed path: a freshly-scraped doc
// whose bytes haven't been downloaded yet is fetched over HTTP exactly as before.
public sealed class LocalFirstDocumentBytesSource : IDocumentBytesSource
{
    private readonly Func<IDocumentBytesSource> _innerFactory;
    private readonly string _downloadsRoot;
    private readonly ILogger<LocalFirstDocumentBytesSource> _logger;

    private readonly Lock _indexLock = new();
    private Dictionary<string, string>? _filenameToPath;

    // Takes a FACTORY for the inner source (not a captured instance) so a fresh
    // typed-client-backed HttpDocumentBytesSource is resolved per HTTP fallback —
    // preserving IHttpClientFactory handler rotation rather than pinning one client
    // for the life of this singleton decorator.
    public LocalFirstDocumentBytesSource(
        Func<IDocumentBytesSource> innerFactory,
        string downloadsRoot,
        ILogger<LocalFirstDocumentBytesSource> logger)
    {
        ArgumentNullException.ThrowIfNull(innerFactory);
        ArgumentException.ThrowIfNullOrEmpty(downloadsRoot);
        ArgumentNullException.ThrowIfNull(logger);
        _innerFactory = innerFactory;
        _downloadsRoot = downloadsRoot;
        _logger = logger;
    }

    public async Task<Stream> OpenAsync(string documentUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUrl);

        var localPath = TryResolveLocal(documentUrl);
        if (localPath is not null)
        {
            _logger.LogDebug("RAG document bytes: local hit for {Url} → {Path}", documentUrl, localPath);
            // Buffer into a seekable MemoryStream (PdfPig needs random access),
            // matching HttpDocumentBytesSource's contract.
            var buffer = new MemoryStream();
            await using (var file = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true))
            {
                await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            buffer.Position = 0;
            return buffer;
        }

        _logger.LogDebug("RAG document bytes: no local file for {Url}; delegating to HTTP source.", documentUrl);
        return await _innerFactory().OpenAsync(documentUrl, cancellationToken).ConfigureAwait(false);
    }

    private string? TryResolveLocal(string documentUrl)
    {
        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }
        var index = GetOrBuildIndex();

        // The on-disk name may be the percent-encoded form (the active downloader's
        // AbsolutePath projection) OR the decoded form. Try both so a file with an
        // escaped char (e.g. %C2%A9) resolves regardless of which projection wrote it.
        var encoded = Path.GetFileName(uri.AbsolutePath);
        var decoded = Uri.UnescapeDataString(encoded);
        foreach (var candidate in new[] { encoded, decoded })
        {
            if (!string.IsNullOrEmpty(candidate)
                && index.TryGetValue(candidate, out var path)
                && File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    private Dictionary<string, string> GetOrBuildIndex()
    {
        lock (_indexLock)
        {
            if (_filenameToPath is not null)
            {
                return _filenameToPath;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_downloadsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(_downloadsRoot, "*", SearchOption.AllDirectories))
                {
                    // Basenames are globally unique in this corpus; first writer wins
                    // on the rare off chance of a duplicate (logged for visibility).
                    var name = Path.GetFileName(path);
                    if (!map.TryAdd(name, path))
                    {
                        _logger.LogWarning(
                            "LocalFirstDocumentBytesSource: duplicate filename '{Name}' under downloads root; keeping {Kept}, ignoring {Ignored}.",
                            name, map[name], path);
                    }

                    // Also index the DECODED form, so a file stored under its encoded
                    // name (e.g. "%C2%A9") and one stored decoded ("©") both resolve.
                    // The lookup tries encoded + UnescapeDataString(encoded), so
                    // indexing the unescaped form covers the decoded-on-disk case.
                    // TryAdd → never clobbers a real (raw) name.
                    var unescaped = Uri.UnescapeDataString(name);
                    if (!string.Equals(unescaped, name, StringComparison.Ordinal))
                    {
                        map.TryAdd(unescaped, path);
                    }
                }
            }
            _logger.LogInformation(
                "LocalFirstDocumentBytesSource: indexed {Count} local files under {Root}.", map.Count, _downloadsRoot);
            _filenameToPath = map;
            return map;
        }
    }
}
