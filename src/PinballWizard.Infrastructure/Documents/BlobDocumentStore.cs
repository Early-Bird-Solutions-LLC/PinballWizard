using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Documents;

namespace PinballWizard.Infrastructure.Documents;

// Blob-backed implementation of IDocumentBlobStore targeting the
// 'pinwiz-raw' container. Registered as a singleton by
// BlobDocumentStoreRegistration; the BlobContainerClient is constructed
// there and injected here to keep this class testable without a real
// storage account.
//
// OpenReadAsync/TryOpenReadAsync hand back a seekable temp-file-backed
// FileStream (DeleteOnClose) so callers (PdfPig text extractor, SHA-256
// backfill) get random access without the blob ever being materialized on
// the heap (#832). See DownloadToTempFileAsync for the memory/disk budget.
public sealed class BlobDocumentStore : IDocumentBlobStore
{
    public const string ContainerName = "pinwiz-raw";

    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobDocumentStore> _logger;

    public BlobDocumentStore(
        BlobContainerClient container,
        ILogger<BlobDocumentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);
        _container = container;
        _logger = logger;
    }

    public async Task WriteAsync(
        string blobName,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(content);

        _logger.LogDebug("BlobDocumentStore: writing blob '{BlobName}'", blobName);
        await _container.GetBlobClient(blobName)
            .UploadAsync(content, overwrite: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var response = await _container.GetBlobClient(blobName)
            .ExistsAsync(cancellationToken)
            .ConfigureAwait(false);
        return response.Value;
    }

    public async Task<long?> GetSizeAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        try
        {
            var properties = await _container.GetBlobClient(blobName)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return properties.Value.ContentLength;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        _logger.LogDebug("BlobDocumentStore: opening blob '{BlobName}'", blobName);

        // Azure.RequestFailedException with Status=404 propagates to the
        // caller when the blob does not exist — callers treat 404 as "not
        // yet downloaded" rather than a hard error.
        return await DownloadToTempFileAsync(blobName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> TryOpenReadAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        _logger.LogDebug("BlobDocumentStore: trying to open blob '{BlobName}'", blobName);

        // Absorb 404 here (Infrastructure layer) so Application callers never
        // need to reference Azure.RequestFailedException (an Azure SDK type).
        // Any non-404 storage error still propagates — Invariant #17: a read
        // error is not silently swallowed as "not available".
        try
        {
            return await DownloadToTempFileAsync(blobName, cancellationToken).ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("BlobDocumentStore: blob '{BlobName}' not found (404) — treating as miss.", blobName);
            return null;
        }
    }

    // #832: download into a temp FileStream instead of a MemoryStream, so
    // peak memory is O(copy buffer) regardless of blob size. The previous
    // MemoryStream buffering reasoned about ONE document fitting in the ACA
    // container's memory; it never accounted for concurrent extractions, and
    // MemoryStream's doubling growth transiently costs old+new buffers on the
    // LOH (the Azure SDK's PartitionedDownloader never pre-sizes the
    // destination — verified at tag Azure.Storage.Blobs_12.29.1).
    //
    // DeleteOnClose on Linux unlinks at DISPOSE (SafeFileHandle.ReleaseHandle
    // "mimics" the flag), not at open — so a SIGKILL leaves the file. That is
    // acceptable by construction: ACA container-scoped storage "disappears
    // when the container shuts down or restarts" (Microsoft Learn, storage
    // mounts), so an orphan can never outlive the failed execution. Budget:
    // ExtractionConcurrency(4) x MaxStreamBytes(100 MB) = 400 MB, inside the
    // 2 GiB ephemeral allowance at <=0.5 vCPU.
    private async Task<FileStream> DownloadToTempFileAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await _container.GetBlobClient(blobName)
                .DownloadToAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            // Disposing closes the handle; DeleteOnClose deletes the file
            // automatically — no File.Delete needed.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
