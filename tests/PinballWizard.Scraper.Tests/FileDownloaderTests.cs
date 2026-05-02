using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Downloading;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests;

/// <summary>
/// Defends conditional-download semantics, hashing, size guard, and error handling
/// in <see cref="FileDownloader"/>. Uses a stub <see cref="HttpMessageHandler"/> so
/// no live network calls are made.
/// </summary>
public sealed class FileDownloaderTests : IDisposable
{
    private readonly string _tempDir;

    public FileDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pinballwizard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private (FileDownloader downloader, StubHandler handler, ScraperSettings settings) CreateDownloader(
        Action<HttpRequestMessage, HttpResponseMessage>? configureResponse = null,
        long maxFileSize = 500 * 1024 * 1024)
    {
        var handler = new StubHandler(configureResponse);
        var httpClient = new HttpClient(handler);
        var settings = new ScraperSettings
        {
            DataPath = _tempDir,
            MaxFileSizeBytes = maxFileSize
        };
        var downloader = new FileDownloader(
            httpClient,
            Options.Create(settings),
            NullLogger<FileDownloader>.Instance);
        return (downloader, handler, settings);
    }

    [Fact]
    public async Task DownloadAsync_304NotModified_ReturnsNotModifiedAndDoesNotWriteToDisk()
    {
        var (downloader, _, settings) = CreateDownloader((_, response) =>
        {
            response.StatusCode = HttpStatusCode.NotModified;
        });

        var result = await downloader.DownloadAsync(
            "https://sternpinball.com/x.pdf",
            "manuals/x.pdf",
            previousMetadata: new HttpMetadata { ETag = "\"abc\"" });

        Assert.Equal(DownloadStatus.NotModified, result.Status);
        var absolute = Path.Combine(settings.DownloadsPath, "manuals/x.pdf");
        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task DownloadAsync_200_StreamsToDiskComputesHashAndPopulatesHttp()
    {
        const string body = "hello pinball";
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var (downloader, _, settings) = CreateDownloader((_, response) =>
        {
            response.StatusCode = HttpStatusCode.OK;
            response.Content = new ByteArrayContent(bodyBytes);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            response.Content.Headers.ContentLength = bodyBytes.Length;
            response.Content.Headers.LastModified = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
            response.Headers.ETag = new EntityTagHeaderValue("\"etag-value\"");
        });

        var result = await downloader.DownloadAsync(
            "https://sternpinball.com/foo.pdf",
            "manuals/foo.pdf");

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(bodyBytes.Length, result.SizeBytes);
        Assert.Equal("foo.pdf", result.Filename);

        // SHA-256 of "hello pinball"
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bodyBytes)).ToLowerInvariant();
        Assert.Equal(expectedHash, result.Sha256);

        Assert.NotNull(result.Http);
        Assert.Equal("\"etag-value\"", result.Http!.ETag);
        Assert.Equal("application/pdf", result.Http.ContentType);
        Assert.NotNull(result.Http.LastModified);

        // File written
        var absolute = Path.Combine(settings.DownloadsPath, "manuals/foo.pdf");
        Assert.True(File.Exists(absolute));
        Assert.Equal(body, await File.ReadAllTextAsync(absolute));
    }

    [Fact]
    public async Task DownloadAsync_WithPreviousMetadata_SendsConditionalHeaders()
    {
        HttpRequestMessage? captured = null;
        var (downloader, _, _) = CreateDownloader((req, response) =>
        {
            captured = req;
            response.StatusCode = HttpStatusCode.NotModified;
        });

        var lastModified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await downloader.DownloadAsync(
            "https://sternpinball.com/x.pdf",
            "manuals/x.pdf",
            previousMetadata: new HttpMetadata
            {
                ETag = "\"prev-etag\"",
                LastModified = lastModified
            });

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("If-None-Match", out var ifNoneMatch));
        Assert.Contains("\"prev-etag\"", ifNoneMatch!);
        Assert.NotNull(captured.Headers.IfModifiedSince);
        Assert.Equal(lastModified, captured.Headers.IfModifiedSince!.Value.UtcDateTime);
    }

    [Fact]
    public async Task DownloadAsync_NoPreviousMetadata_NoConditionalHeaders()
    {
        HttpRequestMessage? captured = null;
        var (downloader, _, _) = CreateDownloader((req, response) =>
        {
            captured = req;
            response.StatusCode = HttpStatusCode.OK;
            response.Content = new ByteArrayContent([0x01, 0x02, 0x03]);
            response.Content.Headers.ContentLength = 3;
        });

        await downloader.DownloadAsync(
            "https://sternpinball.com/x.pdf",
            "manuals/x.pdf",
            previousMetadata: null);

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("If-None-Match"));
        Assert.Null(captured.Headers.IfModifiedSince);
    }

    [Fact]
    public async Task DownloadAsync_ContentLengthExceedsMax_ReturnsTooLargeAndDoesNotWrite()
    {
        // Cap below the declared content-length so the size guard trips
        var (downloader, _, settings) = CreateDownloader((_, response) =>
        {
            response.StatusCode = HttpStatusCode.OK;
            response.Content = new ByteArrayContent(new byte[10]);
            response.Content.Headers.ContentLength = 10_000_000;
        }, maxFileSize: 1024);

        var result = await downloader.DownloadAsync(
            "https://sternpinball.com/huge.zip",
            "manuals/huge.zip");

        Assert.Equal(DownloadStatus.TooLarge, result.Status);
        var absolute = Path.Combine(settings.DownloadsPath, "manuals/huge.zip");
        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task DownloadAsync_NetworkException_ReturnsFailedAndDoesNotThrow()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var httpClient = new HttpClient(handler);
        var settings = new ScraperSettings { DataPath = _tempDir };
        var downloader = new FileDownloader(
            httpClient,
            Options.Create(settings),
            NullLogger<FileDownloader>.Instance);

        var result = await downloader.DownloadAsync(
            "https://sternpinball.com/x.pdf",
            "manuals/x.pdf");

        Assert.Equal(DownloadStatus.Failed, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("connection refused", result.ErrorMessage!);
    }

    [Fact]
    public async Task DownloadAsync_NonSuccessStatus_ReturnsFailed()
    {
        var (downloader, _, _) = CreateDownloader((_, response) =>
        {
            response.StatusCode = HttpStatusCode.NotFound;
            response.Content = new StringContent("not found");
        });

        var result = await downloader.DownloadAsync(
            "https://sternpinball.com/missing.pdf",
            "manuals/missing.pdf");

        Assert.Equal(DownloadStatus.Failed, result.Status);
    }

    // Retry-related tests previously here moved to the resilience pipeline layer.
    // FileDownloader no longer owns retry — Microsoft.Extensions.Http.Resilience
    // does, registered on the HttpClient in Program.cs. See docs/http-resilience-research.md.

    /// <summary>
    /// Minimal HttpMessageHandler stub: lets the test configure the response
    /// per request via a delegate, with no real network involvement.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage, HttpResponseMessage>? _configure;

        public StubHandler(Action<HttpRequestMessage, HttpResponseMessage>? configure)
        {
            _configure = configure;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            };
            _configure?.Invoke(request, response);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    // SequencedHandler / CountingStatusHandler removed along with their retry tests
    // (history preserved in git). The remaining tests use StubHandler / ThrowingHandler.
}
