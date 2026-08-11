using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PinballWizard.Application.Linking;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// Tests for <c>LinkDocumentsCommand.RunAsync</c>.
///
/// <c>LinkDocumentsCommand</c> is <c>internal</c>; tests invoke it through
/// reflection via <see cref="Type.GetType(string)"/> so no production source
/// file needs modification. The observable contract — what gets called on
/// <see cref="IDocumentLinker"/> and what exit-codes / console output are
/// produced — is what matters.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class LinkDocumentsCommandTests : IDisposable
{
    // Redirect Console so tests don't pollute test output and can assert messages.
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    // Save / restore Environment.ExitCode so tests don't bleed state.
    private readonly int _originalExitCode = Environment.ExitCode;

    public LinkDocumentsCommandTests()
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

    /// <summary>
    /// Invokes <c>LinkDocumentsCommand.RunAsync</c> via reflection so we
    /// can test an <c>internal</c> method without modifying production sources.
    /// Uses the assembly-qualified name to resolve the type across assembly
    /// boundaries, which works even when the type is <c>internal</c>.
    /// </summary>
    private static async Task InvokeRunAsync(
        IServiceProvider services,
        bool relinkAll = false,
        CancellationToken ct = default)
    {
        var type = Type.GetType("PinballWizard.Cli.Commands.LinkDocumentsCommand, PinballWizard.Cli");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "RunAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // RunAsync(IServiceProvider, CancellationToken, bool relinkAll = false) —
        // reflection requires all parameters; pass relinkAll explicitly.
        var task = (Task)method!.Invoke(null, [services, ct, relinkAll])!;
        await task;
    }

    private static ServiceProvider BuildProviderWith(IDocumentLinker? linker)
    {
        var services = new ServiceCollection();
        if (linker is not null)
            services.AddSingleton(linker);
        return services.BuildServiceProvider();
    }

    // ── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenLinkerNotRegistered_WritesRemediationToStderr()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        Assert.Contains("--link-documents requires Cosmos", _stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenLinkerNotRegistered_SetsExitCode2()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        Assert.Equal(2, Environment.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenLinkerNotRegistered_DoesNotWriteCompletionToStdout()
    {
        using var provider = BuildProviderWith(linker: null);

        await InvokeRunAsync(provider);

        // The "complete" line should never appear when Cosmos is absent.
        Assert.DoesNotContain("complete", _stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenLinkerRegistered_CallsInitializeBeforeRunBatch()
    {
        var linker = Substitute.For<IDocumentLinker>();
        // NSubstitute returns default (0,0,0,0,0) for value tuples automatically;
        // configure it explicitly using positional tuple syntax (no named elements)
        // to satisfy the CS8123-as-error rule in Directory.Build.props.
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns((0, 0, 0, 0, 0, 0));

        using var provider = BuildProviderWith(linker);
        var callOrder = new List<string>();

        linker.When(l => l.InitializeAsync(Arg.Any<CancellationToken>()))
              .Do(_ => callOrder.Add("Initialize"));
        linker.When(l => l.RunBatchAsync(Arg.Any<CancellationToken>()))
              .Do(_ => callOrder.Add("RunBatch"));

        await InvokeRunAsync(provider);

        Assert.Equal(["Initialize", "RunBatch"], callOrder);
    }

    [Fact]
    public async Task RunAsync_WhenLinkerRegistered_WritesCountsToStdout()
    {
        var linker = Substitute.For<IDocumentLinker>();
        // (Processed=10, Linked=7, PlatformGeneric=1, NotInCatalog=1, Failed=1, NeedsReview=0)
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns((10, 7, 1, 1, 1, 0));

        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider);

        var output = _stdout.ToString();
        Assert.Contains("processed=10", output);
        Assert.Contains("linked=7", output);
        Assert.Contains("platform_generic=1", output);
        Assert.Contains("not_in_catalog=1", output);
        Assert.Contains("failed=1", output);
    }

    [Fact]
    public async Task RunAsync_WhenRunBatchReportsFailed_SetsExitCode1()
    {
        var linker = Substitute.For<IDocumentLinker>();
        // (Processed=5, Linked=3, PlatformGeneric=0, NotInCatalog=1, Failed=1, NeedsReview=0)
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns((5, 3, 0, 1, 1, 0));

        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider);

        Assert.Equal(1, Environment.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenRunBatchReturnsZeroFailed_LeavesExitCodeAt0()
    {
        var linker = Substitute.For<IDocumentLinker>();
        // (Processed=5, Linked=5, PlatformGeneric=0, NotInCatalog=0, Failed=0, NeedsReview=0)
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns((5, 5, 0, 0, 0, 0));

        using var provider = BuildProviderWith(linker);
        Environment.ExitCode = 0;

        await InvokeRunAsync(provider);

        Assert.Equal(0, Environment.ExitCode);
    }

    // ── --relink-all fixpoint iteration tests ─────────────────────────────────
    // These three tests verify the core contract added in issue #824.

    /// <summary>
    /// --relink-all drives RunBatchAsync until linked == 0 across passes.
    /// With a linker that returns 3 linked → 2 linked → 0 linked, the loop
    /// must run exactly 3 passes and reset exactly once.
    /// </summary>
    [Fact]
    public async Task RunAsync_RelinkAll_ConvergesInMultiplePasses_CallsRunBatchExactlyNTimes()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.ResetForRelinkAsync(Arg.Any<CancellationToken>()).Returns(200);
        // Returns linked=3, linked=2, linked=0 on successive calls.
        // NSubstitute wraps each value in Task.FromResult for Task<T>-returning methods.
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns(
                  (10, 3, 0, 7, 0, 0),
                  (7, 2, 0, 5, 0, 0),
                  (5, 0, 0, 5, 0, 0));

        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider, relinkAll: true);

        // Reset runs once; RunBatch runs once per pass until convergence.
        await linker.Received(1).ResetForRelinkAsync(Arg.Any<CancellationToken>());
        await linker.Received(3).RunBatchAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// --relink-all prints a per-pass progress line and a final aggregate summary
    /// that names the total pass count and cumulative link count.
    /// </summary>
    [Fact]
    public async Task RunAsync_RelinkAll_PrintsAggregateSummaryWithPassCount()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.ResetForRelinkAsync(Arg.Any<CancellationToken>()).Returns(200);
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns(
                  (10, 3, 0, 7, 0, 0),
                  (7, 2, 0, 5, 0, 0),
                  (5, 0, 0, 5, 0, 0));

        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider, relinkAll: true);

        var output = _stdout.ToString();
        // Final aggregate: 3 passes, cumulative linked = 3+2+0 = 5.
        Assert.Contains("--relink-all complete (3 passes)", output);
        Assert.Contains("linked=5", output);
        // Per-pass progress lines must be present so the nightly log shows convergence.
        Assert.Contains("Pass 1", output);
        Assert.Contains("Pass 2", output);
        Assert.Contains("Pass 3", output);
    }

    /// <summary>
    /// Plain --link-documents (relinkAll=false) runs exactly one RunBatchAsync pass
    /// and never calls ResetForRelinkAsync. The nightly job converges the corpus
    /// incrementally across successive nightly runs, so multi-pass iteration here
    /// is not needed.
    /// </summary>
    [Fact]
    public async Task RunAsync_PlainLinkDocuments_CallsRunBatchExactlyOnce()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns((10, 7, 0, 3, 0, 0));

        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider, relinkAll: false);

        await linker.Received(1).RunBatchAsync(Arg.Any<CancellationToken>());
        await linker.DidNotReceive().ResetForRelinkAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A pathological linker that never returns linked == 0 must be stopped by the
    /// hard pass bound (10 passes). A warning must be written to stderr.
    /// </summary>
    [Fact]
    public async Task RunAsync_RelinkAll_WhenLinkerNeverConverges_StopsAtHardBound()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.ResetForRelinkAsync(Arg.Any<CancellationToken>()).Returns(100);
        // Always reports linked=1 — would loop forever without the bound.
        linker.RunBatchAsync(Arg.Any<CancellationToken>())
              .Returns((5, 1, 0, 4, 0, 0));

        using var provider = BuildProviderWith(linker);

        await InvokeRunAsync(provider, relinkAll: true);

        // The hard limit is 10 passes (RelinkMaxPasses, private const).
        await linker.Received(10).RunBatchAsync(Arg.Any<CancellationToken>());
        // Operator must be informed that convergence was not reached.
        Assert.Contains("hard limit", _stderr.ToString());
    }
}
