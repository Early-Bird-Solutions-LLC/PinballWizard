using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// Tests for <c>CaptureGoldenSetCommand.RunGoldenLinkSetAsync</c>.
///
/// <c>CaptureGoldenSetCommand</c> is <c>internal</c>; tests invoke it through
/// reflection, matching <see cref="LinkDocumentsCommandTests"/>.
///
/// The capture writes its fixture to a path relative to the current directory,
/// so each test runs inside a temp directory and restores the original after —
/// otherwise the command would overwrite the real captured fixture in the repo.
///
/// Directory.SetCurrentDirectory is process-global, so this is only safe because
/// the "ConsoleCapture" collection is declared DisableParallelization = true (see
/// ConsoleCaptureMarker) and therefore never runs alongside another collection.
/// Moving this class out of that collection would let a concurrent test observe
/// the mutated working directory. Keep it here.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class CaptureGoldenSetCommandTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly string _originalDirectory;
    private readonly string _tempDirectory;
    private readonly int _originalExitCode = Environment.ExitCode;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public CaptureGoldenSetCommandTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        Environment.ExitCode = 0;

        _originalDirectory = Directory.GetCurrentDirectory();
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pinwiz-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        Directory.SetCurrentDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Environment.ExitCode = _originalExitCode;
        _stdout.Dispose();
        _stderr.Dispose();

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp dir; never fail a test over it.
        }
    }

    // The authoritative document→machine binding is the scraped_documents fan-out row the
    // linker writes — not RawDocument, which never carried this binding (the dead
    // linked_machine_ids field on scraped_documents_raw was removed in #800). Capturing
    // from the fan-out is the only correct path; this test exercises that path directly.
    [Fact]
    public async Task RunGoldenLinkSet_CapturesEntries_FromFanOutRows_NotRawLinkedMachineIds()
    {
        var raw = MakeRaw("doc-1", "https://sternpinball.com/godzilla-manual.pdf", "godzilla", "stern");

        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo.StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsAsync(raw));

        var scrapedRepo = Substitute.For<IScrapedDocumentRepository>();
        scrapedRepo.StreamByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>())
            .Returns(_ => AsAsync("GweeP-MW95j", "GweeP-MB2Lk"));

        using var provider = BuildProvider(rawRepo, scrapedRepo);
        await InvokeGoldenLinkSetAsync(provider);

        var fixture = ReadFixture();

        Assert.Equal(1, fixture.DocumentCount);
        Assert.Equal(2, fixture.EntryCount);
        Assert.Equal(2, fixture.Entries.Count);
        Assert.Equal(
            ["GweeP-MB2Lk", "GweeP-MW95j"],
            fixture.Entries.Select(e => e.ExpectedMachineId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(fixture.Entries, e => Assert.Equal("doc-1", e.DocumentId));
        Assert.All(fixture.Entries, e => Assert.Equal("godzilla", e.GameSlug));
    }

    // A linked document with no fan-out row contributes no entries but must still be
    // counted as a document — the gap between DocumentCount and EntryCount is the
    // signal an operator reads to spot an incomplete fan-out.
    [Fact]
    public async Task RunGoldenLinkSet_CountsDocument_ButEmitsNoEntry_WhenFanOutRowsAbsent()
    {
        var raw = MakeRaw("doc-orphan", "https://sternpinball.com/orphan.pdf", "orphan", "stern");

        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo.StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsAsync(raw));

        var scrapedRepo = Substitute.For<IScrapedDocumentRepository>();
        scrapedRepo.StreamByDocumentIdAsync("doc-orphan", Arg.Any<CancellationToken>())
            .Returns(_ => AsAsync<string>());

        using var provider = BuildProvider(rawRepo, scrapedRepo);
        await InvokeGoldenLinkSetAsync(provider);

        var fixture = ReadFixture();

        Assert.Equal(1, fixture.DocumentCount);
        Assert.Equal(0, fixture.EntryCount);
        Assert.Empty(fixture.Entries);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task InvokeGoldenLinkSetAsync(IServiceProvider services)
    {
        var type = Type.GetType("PinballWizard.Cli.Commands.CaptureGoldenSetCommand, PinballWizard.Cli");
        Assert.NotNull(type);

        var method = type!.GetMethod("RunGoldenLinkSetAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(null, [services, CancellationToken.None])!;
        await task;
    }

    private static ServiceProvider BuildProvider(
        IRawDocumentRepository rawRepo,
        IScrapedDocumentRepository scrapedRepo)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rawRepo);
        services.AddSingleton(scrapedRepo);
        return services.BuildServiceProvider();
    }

    private static CapturedFixture ReadFixture()
    {
        var path = Path.Combine(
            "tests", "PinballWizard.Application.Tests", "Fixtures", "Linking", "golden-link-set.captured.json");
        Assert.True(File.Exists(path), $"Capture did not write the fixture at {path}.");

        return JsonSerializer.Deserialize<CapturedFixture>(File.ReadAllText(path), ReadOptions)!;
    }

    private static RawDocumentRecord MakeRaw(
        string documentId, string fileUrl, string gameSlug, string manufacturer)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = DocumentType.Manual,
            Manufacturer = manufacturer,
            LinkStatus = LinkStatus.Linked,
            Game = new GameReference
            {
                Title = gameSlug.Replace('-', ' '),
                Slug = gameSlug,
                GamePageUrl = $"https://example.com/{manufacturer}/game/{gameSlug}/",
            },
            Source = new SourceInfo
            {
                DiscoveryUrl = $"https://example.com/{manufacturer}/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = fileUrl,
                SourceType = SourceType.ManualsPage,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc) },
        };

    // Matches ReconcilerParityReplayTests.ToAsync — yields between items so the
    // iterator actually suspends, exercising the consumer's await path.
    private static async IAsyncEnumerable<T> AsAsync<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private sealed class CapturedFixture
    {
        public int DocumentCount { get; set; }
        public int EntryCount { get; set; }
        public List<CapturedEntry> Entries { get; set; } = [];
    }

    private sealed class CapturedEntry
    {
        public string DocumentId { get; set; } = string.Empty;
        public string? GameSlug { get; set; }
        public string ExpectedMachineId { get; set; } = string.Empty;
    }
}
