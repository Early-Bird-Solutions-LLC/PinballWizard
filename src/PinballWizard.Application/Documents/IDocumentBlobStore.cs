namespace PinballWizard.Application.Documents;

public interface IDocumentBlobStore
{
    Task WriteAsync(string blobName, Stream content, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string blobName, CancellationToken cancellationToken);

    // Content length via a blob-properties call (no body download). Returns
    // null if the blob is absent — callers should treat this as "unknown",
    // not an error.
    Task<long?> GetSizeAsync(string blobName, CancellationToken cancellationToken);

    // Seekable, buffered stream the caller disposes; throws if the blob is absent.
    Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken);

    // Seekable, buffered stream the caller disposes; returns null if the blob is absent
    // (404). Other storage errors still propagate. Keeps Azure SDK exception types out
    // of Application-layer callers by absorbing only the "not yet downloaded" miss case.
    Task<Stream?> TryOpenReadAsync(string blobName, CancellationToken cancellationToken);
}
