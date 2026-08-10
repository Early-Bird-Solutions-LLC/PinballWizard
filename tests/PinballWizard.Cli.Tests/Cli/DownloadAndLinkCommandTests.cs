using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// Tests for <c>DownloadAndLinkCommand.RunAsync</c>.
///
/// <c>DownloadAndLinkCommand</c> is <c>internal</c>; tests invoke it through
/// reflection so no production source file needs modification.
///
/// The observable contract: only a MISSING download service (exit code 2)
/// skips the link stage — the linker resolves the same Cosmos wiring and
/// would fail identically. A partial per-document download failure (exit
/// code 1) still runs the link stage, since the linker degrades gracefully
/// when a file is absent (issue #647 — a handful of expected download
/// failures used to skip linking the entire corpus).
///
/// <c>DocumentDownloadService</c> is sealed against subclassing but takes
/// only interface dependencies, so the "download stage absent" scenarios
/// build no service at all (exit code 2), while the "partial failure"
/// scenario constructs a real instance over substituted
/// <see cref="IFileDownloader"/>/<see cref="IRawDocumentRepository"/>/
/// <see cref="IDocumentBlobStore"/> deps to drive a real exit-code-1 run.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class DownloadAndLinkCommandTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly int _originalExitCode = Environment.ExitCode;

    public DownloadAndLinkCommandTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        Environment.ExitCode = 0;
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Environment.ExitCode = _originalExitCode;
        _stdout.Dispose();
        _stderr.Dispose();
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task InvokeRunAsync(
        IServiceProvider services,
        bool force = false,
        CancellationToken ct = default)
    {
        var type = Type.GetType("PinballWizard.Cli.Commands.DownloadAndLinkCommand, PinballWizard.Cli");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "RunAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(null, [services, ct, force])!;
        await task;
    }

    private static ServiceProvider BuildProviderWith(IDocumentLinker? linker)
    {
        // DocumentDownloadService is not registered here, which causes the
        // download stage to set exit code 2 and write a remediation message —
        // the "absent service" path tested by DownloadDocumentsCommandTests.
        var services = new ServiceCollection();
        if (linker is not null)
            services.AddSingleton(linker);
        return services.BuildServiceProvider();
    }

    // Builds a real DocumentDownloadService (not registered — instantiated over
    // substituted interface deps) whose single document reports a per-file
    // failure, driving a genuine exit-code-1 run (not the exit-code-2 "service
    // absent" path used by the other fixtures in this file).
    private static ServiceProvider BuildProviderWithPartialDownloadFailure(IDocumentLinker linker)
    {
        var repo = Substitute.For<IRawDocumentRepository>();
        var downloader = Substitute.For<IFileDownloader>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var raw = new RawDocumentRecord
        {
            DocumentId = "doc_a",
            DocumentUrl = "https://sternpinball.com/manuals/x.pdf",
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals page",
                FileUrl = "https://sternpinball.com/manuals/x.pdf",
                ScrapedAt = DateTime.UtcNow,
                SourceType = SourceType.ManualsPage,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
        };
        repo.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(ToAsync(raw));
        downloader.DownloadAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Failed,
                FileUrl = raw.Source.FileUrl!,
                LocalPath = "manualspage/x.pdf",
                ErrorMessage = "404 Not Found",
            });

        var downloadService = new DocumentDownloadService(
            repo, downloader, blobStore, NullLogger<DocumentDownloadService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(downloadService);
        services.AddSingleton(linker);
        return services.BuildServiceProvider();
    }

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsync(params RawDocumentRecord[] docs)
    {
        foreach (var d in docs) { yield return d; await Task.Yield(); }
    }

    // ── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenDownloaderNotRegistered_WritesRemediationToStderr()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        Assert.Contains("--download-documents requires Cosmos", _stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenDownloaderNotRegistered_SetsExitCode2()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        Assert.Equal(2, Environment.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenDownloadServiceAbsent_DoesNotCallLinkStage()
    {
        // When the download stage reports a missing service (exit code 2 —
        // downloader absent), the link stage must not run: it resolves the
        // same Cosmos wiring and would fail identically.
        var linker = Substitute.For<IDocumentLinker>();
        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider);

        await linker.DidNotReceive().InitializeAsync(Arg.Any<CancellationToken>());
        await linker.DidNotReceive().RunBatchAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenDownloadServiceAbsent_DoesNotWriteLinkCompletionToStdout()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        // The "--link-documents complete" line must not appear.
        Assert.DoesNotContain("--link-documents complete", _stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenDownloadServiceAbsent_DoesNotWriteDownloadCompletionToStdout()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        // The "--download-documents complete" line must not appear when the service
        // is absent (exit code 2 path skips the success banner).
        Assert.DoesNotContain("--download-documents complete", _stdout.ToString());
    }

    // Reproduces issue #647: a handful of expected per-document download
    // failures (dead links, transient errors) must not skip the link stage
    // for the whole corpus.
    [Fact]
    public async Task RunAsync_WhenDownloadHasPartialFailures_StillCallsLinkStage()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.RunBatchAsync(Arg.Any<CancellationToken>()).Returns((0, 0, 0, 0, 0, 0));
        using var provider = BuildProviderWithPartialDownloadFailure(linker);

        await InvokeRunAsync(provider);

        await linker.Received(1).InitializeAsync(Arg.Any<CancellationToken>());
        await linker.Received(1).RunBatchAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenDownloadHasPartialFailures_PropagatesExitCode1()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.RunBatchAsync(Arg.Any<CancellationToken>()).Returns((0, 0, 0, 0, 0, 0));
        using var provider = BuildProviderWithPartialDownloadFailure(linker);

        await InvokeRunAsync(provider);

        // Download set exit code 1 (summary.Failed > 0); the link stage's own
        // failed=0 doesn't reset it, so the combined run still reports failure.
        Assert.Equal(1, Environment.ExitCode);
    }
}
