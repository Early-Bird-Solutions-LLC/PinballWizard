using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Documents;
using PinballWizard.Infrastructure.Documents;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Documents;

// Two test classes: a pure-unit class that always runs (covering the
// container-name constant and blob-name passthrough), and an Azurite
// round-trip class guarded by the AZURITE_BLOB_SERVICE_URL environment
// variable so CI without a running Azurite emulator skips it cleanly.
//
// Why AZURITE_BLOB_SERVICE_URL? Azurite's dev-storage connection string
// (UseDevelopmentStorage=true) hard-wires the service to 127.0.0.1:10000.
// That port is not guaranteed available in every CI agent. Using an env
// var lets the caller point at any Azurite instance (local, Docker, the
// Aspire-managed one) without code changes. When the variable is absent,
// [RequiresAzuriteFact] sets Skip so xUnit reports the tests as Skipped
// (not Passed); when it is present the tests run for real and exercise
// the full IO path.
public sealed class BlobDocumentStoreTests
{
    // The container name is the sealed behavioral contract — callers that
    // pass a BlobDocumentStore into the RAG pipeline expect "pinwiz-raw"
    // to be the target without having to know or pass the container name.
    [Fact]
    public void ContainerName_IsExpectedValue()
    {
        Assert.Equal("pinwiz-raw", BlobDocumentStore.ContainerName);
    }

    // Blob-name passthrough: the store must route every write/exists/read
    // call to the blob named by the caller without transforming the name.
    // Uses a real BlobContainerClient pointed at a non-existent host so
    // the client is correctly constructed but no network I/O occurs.
    [RequiresAzuriteFact]
    public async Task ExistsAsync_ReturnsFalse_ForNonExistentBlobWhenAzuriteAvailable()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;

        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerName = $"test-{Guid.NewGuid():N}";
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var exists = await sut.ExistsAsync("does-not-exist.pdf", CancellationToken.None);

            Assert.False(exists);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    [RequiresAzuriteFact]
    public async Task WriteThenOpenRead_RoundTripsBytes()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;

        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerName = $"test-{Guid.NewGuid():N}";
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var expected = new byte[] { 1, 2, 3, 4, 5 };
            const string blobName = "round-trip.bin";

            using var writeStream = new MemoryStream(expected);
            await sut.WriteAsync(blobName, writeStream, CancellationToken.None);

            // After writing, ExistsAsync must return true.
            Assert.True(await sut.ExistsAsync(blobName, CancellationToken.None));

            // OpenReadAsync returns a seekable stream with identical bytes.
            using var readStream = await sut.OpenReadAsync(blobName, CancellationToken.None);

            Assert.True(readStream.CanSeek, "OpenReadAsync must return a seekable stream");
            var actual = new byte[expected.Length];
            var bytesRead = await readStream.ReadAsync(actual);
            Assert.Equal(expected.Length, bytesRead);
            Assert.Equal(expected, actual);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    [RequiresAzuriteFact]
    public async Task GetSizeAsync_ExistingBlob_ReturnsContentLength()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;

        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerName = $"test-{Guid.NewGuid():N}";
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
            const string blobName = "sized.bin";
            using var writeStream = new MemoryStream(bytes);
            await sut.WriteAsync(blobName, writeStream, CancellationToken.None);

            var size = await sut.GetSizeAsync(blobName, CancellationToken.None);

            Assert.Equal(bytes.Length, size);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    [RequiresAzuriteFact]
    public async Task GetSizeAsync_AbsentBlob_ReturnsNull()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;

        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerName = $"test-{Guid.NewGuid():N}";
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var size = await sut.GetSizeAsync("does-not-exist.bin", CancellationToken.None);

            Assert.Null(size);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    [RequiresAzuriteFact]
    public async Task OpenReadAsync_AbsentBlob_ThrowsRequestFailedException404()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;

        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerName = $"test-{Guid.NewGuid():N}";
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var ex = await Assert.ThrowsAsync<RequestFailedException>(
                () => sut.OpenReadAsync("absent.pdf", CancellationToken.None));

            Assert.Equal(404, ex.Status);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    // --- #832 temp-file backing ------------------------------------------------

    [RequiresAzuriteFact]
    public async Task OpenReadAsync_ReturnsSeekableFileStream_NotMemoryStream_AndDeletesTempFileOnDispose()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;
        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerClient = serviceClient.GetBlobContainerClient($"test-{Guid.NewGuid():N}");
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);
            var expected = new byte[] { 10, 20, 30, 40 };
            using (var writeStream = new MemoryStream(expected))
                await sut.WriteAsync("temp-backed.bin", writeStream, CancellationToken.None);

            string tempPath;
            var stream = await sut.OpenReadAsync("temp-backed.bin", CancellationToken.None);
            await using (stream)
            {
                // The whole point of #832: the blob must NOT be materialized on
                // the heap. A FileStream is the contract; IsNotType<MemoryStream>
                // is the regression tripwire.
                Assert.IsNotType<MemoryStream>(stream);
                var fileStream = Assert.IsType<FileStream>(stream);
                tempPath = fileStream.Name;

                Assert.True(stream.CanSeek);
                Assert.Equal(0, stream.Position);
                var actual = new byte[expected.Length];
                await stream.ReadExactlyAsync(actual);
                Assert.Equal(expected, actual);
            }

            // DeleteOnClose semantics: the temp file must be gone after dispose.
            // (On Linux the unlink happens at dispose via SafeFileHandle
            // .ReleaseHandle — not at open — so this asserts the only
            // cross-platform guarantee: absence AFTER dispose.)
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }

    [RequiresAzuriteFact]
    public async Task TryOpenReadAsync_MissingBlob_StillReturnsNull_AndLeavesNoTempFile()
    {
        var azuriteUrl = Environment.GetEnvironmentVariable(RequiresAzuriteFactAttribute.EnvVar)!;
        var serviceClient = new BlobServiceClient(azuriteUrl);
        var containerClient = serviceClient.GetBlobContainerClient($"test-{Guid.NewGuid():N}");
        await containerClient.CreateIfNotExistsAsync();

        try
        {
            var sut = new BlobDocumentStore(containerClient, NullLogger<BlobDocumentStore>.Instance);

            var result = await sut.TryOpenReadAsync("does-not-exist.pdf", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            await containerClient.DeleteIfExistsAsync();
        }
    }
}
