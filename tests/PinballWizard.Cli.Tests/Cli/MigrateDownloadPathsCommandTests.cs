using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// Tests for <c>MigrateDownloadPathsCommand.RunAsync</c>.
///
/// <c>MigrateDownloadPathsCommand</c> is <c>internal</c>; tests invoke it through
/// reflection so no production source file needs modification.
///
/// The observable contract under test: the command is DEPRECATED (ADR-0039 — the
/// downloader writes to the pinwiz-raw blob container, not local disk, so there is
/// no on-disk layout left to migrate). It must print a deprecation notice to stderr
/// on every invocation — including when Cosmos is not configured — so an operator
/// who runs it sees the notice rather than silently doing nothing useful.
///
/// <c>DownloadPathMigrationService</c> is a sealed concrete class with no interface,
/// so the test exercises the "service absent" path (no migration work, exit code 2),
/// which is sufficient to assert the deprecation notice fires before the Cosmos check.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class MigrateDownloadPathsCommandTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly int _originalExitCode = Environment.ExitCode;

    public MigrateDownloadPathsCommandTests()
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

    private static async Task InvokeRunAsync(
        IServiceProvider services,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var type = Type.GetType("PinballWizard.Cli.Commands.MigrateDownloadPathsCommand, PinballWizard.Cli");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "RunAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(null, [services, dryRun, ct])!;
        await task;
    }

    [Fact]
    public async Task RunAsync_AlwaysWritesDeprecationNoticeToStderr()
    {
        // No DownloadPathMigrationService registered → "service absent" path. The
        // deprecation notice must still appear (it is emitted before the Cosmos check).
        using var provider = new ServiceCollection().BuildServiceProvider();

        await InvokeRunAsync(provider);

        Assert.Contains("DEPRECATED (ADR-0039)", _stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenServiceAbsent_DoesNotWriteMigrationProgressToStdout()
    {
        // The deprecation no-op path must not print the "Migrating download paths..."
        // working banner — there is nothing to migrate.
        using var provider = new ServiceCollection().BuildServiceProvider();

        await InvokeRunAsync(provider);

        Assert.DoesNotContain("Migrating download paths", _stdout.ToString());
    }
}
