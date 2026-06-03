using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

/// <summary>
/// Tests for <see cref="LocalFirstDocumentBytesSource"/> — a decorator that serves
/// document bytes from the local downloads tree when the file is present (the common
/// case after a download pass), falling back to the inner HTTP source only when the
/// file is absent. Avoids re-fetching byte-verified local PDFs from source sites
/// during a full RAG backfill (faster + politer).
/// </summary>
public sealed class LocalFirstDocumentBytesSourceTests : IDisposable
{
    private readonly string _root;

    public LocalFirstDocumentBytesSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pw-localfirst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task OpenAsync_FilePresentLocally_ReadsLocalBytes_DoesNotCallInner()
    {
        // The file for this URL exists locally under {root}/{sourceType}/{filename}.
        var dir = Path.Combine(_root, "manualspage");
        Directory.CreateDirectory(dir);
        var bytes = Encoding.UTF8.GetBytes("local pdf bytes");
        await File.WriteAllBytesAsync(Path.Combine(dir, "Godzilla_Pro_web.pdf"), bytes);

        var inner = new RecordingInner();
        var sut = new LocalFirstDocumentBytesSource(() => inner, _root, NullLogger<LocalFirstDocumentBytesSource>.Instance);

        await using var stream = await sut.OpenAsync(
            "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf", CancellationToken.None);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
        Assert.True(stream.CanSeek, "stream must be seekable for PdfPig random access");
        Assert.Equal(0, inner.Calls);   // inner HTTP source NOT called
    }

    [Fact]
    public async Task OpenAsync_FileAbsentLocally_DelegatesToInner()
    {
        var inner = new RecordingInner();
        var sut = new LocalFirstDocumentBytesSource(() => inner, _root, NullLogger<LocalFirstDocumentBytesSource>.Instance);

        await using var stream = await sut.OpenAsync(
            "https://sternpinball.com/wp-content/uploads/2022/05/NotDownloaded.pdf", CancellationToken.None);

        Assert.Equal(1, inner.Calls);   // fell back to HTTP
        Assert.Equal("https://sternpinball.com/wp-content/uploads/2022/05/NotDownloaded.pdf", inner.LastUrl);
    }

    [Fact]
    public async Task OpenAsync_FileInNestedSourceTypeDir_FoundByFilename()
    {
        // Files live under per-source-type subdirs; the resolver finds by filename
        // anywhere under the root (basenames are globally unique in this corpus).
        var dir = Path.Combine(_root, "gamepage");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Godzilla-Rulesheet.pdf"), "rules");

        var inner = new RecordingInner();
        var sut = new LocalFirstDocumentBytesSource(() => inner, _root, NullLogger<LocalFirstDocumentBytesSource>.Instance);

        await using var stream = await sut.OpenAsync(
            "https://sternpinball.com/wp-content/uploads/2022/06/Godzilla-Rulesheet.pdf", CancellationToken.None);

        Assert.Equal(0, inner.Calls);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal("rules", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task OpenAsync_PercentEncodedUrl_ResolvesLocalFile_BothEncodedAndDecodedNames()
    {
        // A URL with a percent-encoded char (e.g. %C2%A9 = ©). Depending on which
        // projection produced the on-disk name, the file may be stored encoded
        // ("...%C2%A9...") or decoded ("...©..."). The resolver must find it either
        // way so the optimization isn't silently defeated on such files.
        var dir = Path.Combine(_root, "gamepage");
        Directory.CreateDirectory(dir);
        // On-disk name is the ENCODED form (matches the active downloader's AbsolutePath projection).
        await File.WriteAllTextAsync(Path.Combine(dir, "Matrix-%C2%A9_021226.pdf"), "matrix");

        var inner = new RecordingInner();
        var sut = new LocalFirstDocumentBytesSource(() => inner, _root, NullLogger<LocalFirstDocumentBytesSource>.Instance);

        await using var stream = await sut.OpenAsync(
            "https://sternpinball.com/wp-content/uploads/2026/02/Matrix-%C2%A9_021226.pdf", CancellationToken.None);

        Assert.Equal(0, inner.Calls);   // found locally, no HTTP
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal("matrix", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task OpenAsync_DecodedOnDiskName_ResolvesFromEncodedUrl()
    {
        // Inverse: the on-disk name is the DECODED form (©), the URL is encoded (%C2%A9).
        var dir = Path.Combine(_root, "gamepage");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Matrix-©_021226.pdf"), "decoded");

        var inner = new RecordingInner();
        var sut = new LocalFirstDocumentBytesSource(() => inner, _root, NullLogger<LocalFirstDocumentBytesSource>.Instance);

        await using var stream = await sut.OpenAsync(
            "https://sternpinball.com/wp-content/uploads/2026/02/Matrix-%C2%A9_021226.pdf", CancellationToken.None);

        Assert.Equal(0, inner.Calls);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal("decoded", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task OpenAsync_MalformedUrl_DelegatesToInner_DoesNotThrow()
    {
        var inner = new RecordingInner();
        var sut = new LocalFirstDocumentBytesSource(() => inner, _root, NullLogger<LocalFirstDocumentBytesSource>.Instance);

        await using var stream = await sut.OpenAsync("not-a-valid-url", CancellationToken.None);

        Assert.Equal(1, inner.Calls);   // no local resolution possible → fall back
    }

    private sealed class RecordingInner : IDocumentBytesSource
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<Stream> OpenAsync(string documentUrl, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = documentUrl;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("http bytes")));
        }
    }
}
