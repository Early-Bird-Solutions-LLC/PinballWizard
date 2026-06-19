namespace PinballWizard.Application.Documents;

public interface IDocumentBlobStore
{
    Task WriteAsync(string blobName, Stream content, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string blobName, CancellationToken cancellationToken);

    // Seekable, buffered stream the caller disposes; throws if the blob is absent.
    Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken);
}
