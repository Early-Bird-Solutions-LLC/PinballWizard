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
// OpenReadAsync downloads into a MemoryStream so callers (PdfPig text
// extractor) get a seekable, random-access buffer. The largest raw
// document in scope (~80 MB Stern Godzilla service manual) fits inside
// the ACA container's 1 GiB memory limit with room to spare; revisit
// with a temp-file-backed stream only if substantially larger PDFs land
// in scope. This is the same buffering decision HttpDocumentBytesSource
// makes for the same downstream consumer.
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

    public async Task<Stream> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        _logger.LogDebug("BlobDocumentStore: opening blob '{BlobName}'", blobName);

        // Download into a MemoryStream for seekable random access.
        // Azure.RequestFailedException with Status=404 propagates to the
        // caller when the blob does not exist — callers treat 404 as "not
        // yet downloaded" rather than a hard error.
        var buffer = new MemoryStream();
        await _container.GetBlobClient(blobName)
            .DownloadToAsync(buffer, cancellationToken)
            .ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }
}
