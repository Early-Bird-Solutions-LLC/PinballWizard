using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PinballWizard.Application.Linking;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// Tests for <c>DownloadAndLinkCommand.RunAsync</c>.
///
/// <c>DownloadAndLinkCommand</c> is <c>internal</c>; tests invoke it through
/// reflection so no production source file needs modification.
///
/// The observable contract: when the download stage fails (exit code != 0),
/// the link stage is skipped entirely. Exit codes propagate from whichever
/// stage last set them.
///
/// <c>DocumentDownloadService</c> is a sealed concrete class with no interface,
/// so we exercise the "download stage absent" path (exit code 2, linker not
/// called) to verify the skip-on-failure composition. The "both stages succeed"
/// integration path is covered by the full host DI integration tests.
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
        // DocumentDownloadService is a sealed concrete class with no interface;
        // we do not register it here, which causes the download stage to set
        // exit code 2 and write a remediation message — the same "absent service"
        // path tested by DownloadDocumentsCommandTests.
        var services = new ServiceCollection();
        if (linker is not null)
            services.AddSingleton(linker);
        return services.BuildServiceProvider();
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
    public async Task RunAsync_WhenDownloadStageFails_DoesNotCallLinkStage()
    {
        // When the download stage reports failure (exit code 2 — downloader absent),
        // the link stage must not run. This asserts the stage-isolation contract:
        // a failed download does not produce misleading link output.
        var linker = Substitute.For<IDocumentLinker>();
        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider);

        await linker.DidNotReceive().InitializeAsync(Arg.Any<CancellationToken>());
        await linker.DidNotReceive().RunBatchAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenDownloadStageFails_DoesNotWriteLinkCompletionToStdout()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        // The "--link-documents complete" line must not appear.
        Assert.DoesNotContain("--link-documents complete", _stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenDownloadStageFails_DoesNotWriteDownloadCompletionToStdout()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        // The "--download-documents complete" line must not appear when the service
        // is absent (exit code 2 path skips the success banner).
        Assert.DoesNotContain("--download-documents complete", _stdout.ToString());
    }
}
