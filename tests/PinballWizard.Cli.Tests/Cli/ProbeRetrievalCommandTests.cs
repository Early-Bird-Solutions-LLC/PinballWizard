using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

// Tests for ProbeRetrievalCommand.RunAsync.
//
// ProbeRetrievalCommand is internal; tests invoke it through reflection (via
// Type.GetType) so no production source file needs modification. The observable
// contract — exit codes, stderr/stdout messages, and output file content — is
// what is asserted.
//
// The full "probe runs and writes classified.jsonl" path requires a live
// AI Search endpoint and is out of scope for unit tests. The two critical
// behaviors tested here:
//   1. IRetrievalRankProbe absent from DI (AI Search not configured) → verb
//      exits 2 with a remediation message (mirrors --eval / --link-documents).
//   2. Reranker on (CrossEncoderOptions.Enabled=true) → verb exits 2 with a
//      clear explanation that the measurement would be corrupted (reranker-off guard).
[Collection("ConsoleCapture")]
public sealed class ProbeRetrievalCommandTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly int _originalExitCode = Environment.ExitCode;
    private readonly string _tempDir;

    public ProbeRetrievalCommandTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        Environment.ExitCode = 0;

        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "pinballwizard-probe-retrieval-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Environment.ExitCode = _originalExitCode;
        _stdout.Dispose();
        _stderr.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup; test isolation does not depend on removal.
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task InvokeRunAsync(
        string inputPath,
        IServiceProvider services,
        CancellationToken ct = default)
    {
        var type = Type.GetType("PinballWizard.Cli.Commands.ProbeRetrievalCommand, PinballWizard.Cli");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "RunAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(null, [inputPath, services, ct])!;
        await task;
    }

    private static ServiceProvider BuildProviderWith(
        IRetrievalRankProbe? probe,
        bool rerankerEnabled = false)
    {
        var services = new ServiceCollection();
        if (probe is not null)
            services.AddSingleton(probe);

        // Register CrossEncoderOptions so the reranker-guard can read it.
        var crossEncoderOptions = new CrossEncoderOptions
        {
            Enabled = rerankerEnabled,
            TopN = 5,
        };
        services.AddSingleton<IOptions<CrossEncoderOptions>>(
            new OptionsWrapper<CrossEncoderOptions>(crossEncoderOptions));

        return services.BuildServiceProvider();
    }

    // ── tests: probe-absent (AI Search not configured) ─────────────────────────

    [Fact]
    public async Task RunAsync_WhenProbeNotRegistered_WritesRemediationToStderr()
    {
        using var provider = BuildProviderWith(probe: null);
        var inputPath = Path.Combine(_tempDir, "test.jsonl");

        await InvokeRunAsync(inputPath, provider);

        Assert.Contains("--probe-retrieval requires Azure AI Search", _stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenProbeNotRegistered_SetsExitCode2()
    {
        using var provider = BuildProviderWith(probe: null);
        var inputPath = Path.Combine(_tempDir, "test.jsonl");

        await InvokeRunAsync(inputPath, provider);

        Assert.Equal(2, Environment.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenProbeNotRegistered_DoesNotWriteToStdout()
    {
        using var provider = BuildProviderWith(probe: null);
        var inputPath = Path.Combine(_tempDir, "test.jsonl");

        await InvokeRunAsync(inputPath, provider);

        Assert.Empty(_stdout.ToString());
    }

    // ── tests: reranker-on guard ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenRerankerEnabled_WritesRerankerGuardToStderr()
    {
        var probe = Substitute.For<IRetrievalRankProbe>();
        using var provider = BuildProviderWith(probe, rerankerEnabled: true);
        var inputPath = Path.Combine(_tempDir, "test.jsonl");

        await InvokeRunAsync(inputPath, provider);

        Assert.Contains("Rag:CrossEncoder:Enabled", _stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenRerankerEnabled_SetsExitCode2()
    {
        var probe = Substitute.For<IRetrievalRankProbe>();
        using var provider = BuildProviderWith(probe, rerankerEnabled: true);
        var inputPath = Path.Combine(_tempDir, "test.jsonl");

        await InvokeRunAsync(inputPath, provider);

        Assert.Equal(2, Environment.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenRerankerEnabled_DoesNotCallProbe()
    {
        var probe = Substitute.For<IRetrievalRankProbe>();
        using var provider = BuildProviderWith(probe, rerankerEnabled: true);
        var inputPath = Path.Combine(_tempDir, "test.jsonl");

        await InvokeRunAsync(inputPath, provider);

        // The probe must not be called when the reranker-on guard fires.
        await probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default, default);
    }

    // ── tests: BuildOutputPath helper ──────────────────────────────────────────

    [Theory]
    [InlineData("data/eval/wizard.v2.jsonl", "data/eval/wizard.v2.classified.jsonl")]
    [InlineData("/abs/path/input.jsonl", "/abs/path/input.classified.jsonl")]
    [InlineData("flat.jsonl", "flat.classified.jsonl")]
    public void BuildOutputPath_AppendsClassifiedSuffix(string input, string expected)
    {
        var type = Type.GetType("PinballWizard.Cli.Commands.ProbeRetrievalCommand, PinballWizard.Cli");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "BuildOutputPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = (string)method!.Invoke(null, [input])!;

        // Normalize separators so the test is cross-platform.
        Assert.Equal(
            expected.Replace('/', Path.DirectorySeparatorChar),
            actual.Replace('/', Path.DirectorySeparatorChar));
    }
}
