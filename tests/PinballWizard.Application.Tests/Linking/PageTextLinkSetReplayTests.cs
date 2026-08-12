using System.Text;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Tests.Fixtures;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using System.Text.Json;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// #832 page-tier regression gate. The slug-only golden replay runs with
// previewExtractor: null, so tiers 3/4 NEVER execute there — this suite is the
// only offline coverage those tiers have. Two tiers of tests, mirroring
// GoldenLinkSetReplayTests: synthetic harness proofs (always run) and a
// live-fixture replay gated on the captured file.
public sealed class PageTextLinkSetReplayTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    internal const string CapturedFixtureRepoPath =
        "tests/PinballWizard.Application.Tests/Fixtures/Linking/page-text-link-set.captured.json";

    // ── Replay plumbing ────────────────────────────────────────────────────────

    // The linker hands the extractor a bare Stream; identity travels as the
    // stream CONTENT (the fake blob store writes the blob name into it).
    private sealed class FixturePreviewExtractor(
        IReadOnlyDictionary<string, IReadOnlyList<ExtractedPage>> pagesByBlobName) : IDocumentPreviewExtractor
    {
        public async Task<ExtractedPreview> ExtractPreviewAsync(Stream pdfStream, int pageCount, CancellationToken ct)
        {
            using var reader = new StreamReader(pdfStream, Encoding.UTF8, leaveOpen: true);
            var blobName = await reader.ReadToEndAsync(ct);
            return pagesByBlobName.TryGetValue(blobName, out var pages)
                ? new ExtractedPreview(ExtractionStatus.Success, pages.Take(pageCount).ToList(), Error: null)
                : ExtractedPreview.Failure(ExtractionStatus.Malformed, $"no fixture entry for blob '{blobName}'");
        }
    }

    private static IDocumentBlobStore MakeFixtureBlobStore()
    {
        var blobStore = Substitute.For<IDocumentBlobStore>();
        // Non-null small size is LOAD-BEARING: NSubstitute's Task<long?> default
        // is null, which the #832 size guard classifies as blob_missing — every
        // entry would silently skip and the gate would be vacuous.
        blobStore.GetSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1024L);
        blobStore.TryOpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (Stream?)new MemoryStream(Encoding.UTF8.GetBytes(call.Arg<string>())));
        return blobStore;
    }

    // ── Synthetic tests (always run — prove the gate can fail before live data) ──

    private static readonly Machine SynthGodzilla = LinkerReplayHarness.MakeMachine(
        id: "SYNTH-GZ-01", manufacturerKey: "stern", title: "Godzilla", slug: "godzilla-synth");

    private static readonly Machine SynthOktoberfest = LinkerReplayHarness.MakeMachine(
        id: "SYNTH-OK-01", manufacturerKey: "americanpinball", title: "Oktoberfest", slug: "oktoberfest-synth");

    [Fact]
    public async Task Synthetic_PageTextResolvesToExpectedMachine()
    {
        var extractor = new FixturePreviewExtractor(new Dictionary<string, IReadOnlyList<ExtractedPage>>
        {
            ["manualspage/gz.pdf"] = [new ExtractedPage(1, "Godzilla Service Manual — Stern Pinball")],
        });
        using var linker = await LinkerReplayHarness.BuildLinkerAsync(
            [SynthGodzilla, SynthOktoberfest], extractor, MakeFixtureBlobStore());

        var raw = LinkerReplayHarness.MakeRaw(
            documentId: "doc_synth_page", fileUrl: "https://example.com/gz.pdf",
            gameSlug: string.Empty, manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage, localPath: "manualspage/gz.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Contains("SYNTH-GZ-01", result.LinkedMachineIds);
        Assert.StartsWith("page_1", result.ResolutionStrategy);
    }

    [Fact]
    public async Task Synthetic_MisattributionIsDetectable()
    {
        // The gate's reason to exist: feed page text naming machine A, expect
        // machine B, and confirm the replay CAN observe the divergence. If this
        // test ever passes with the divergence undetected, the gate is vacuous.
        var extractor = new FixturePreviewExtractor(new Dictionary<string, IReadOnlyList<ExtractedPage>>
        {
            ["manualspage/gz.pdf"] = [new ExtractedPage(1, "Godzilla Service Manual — Stern Pinball")],
        });
        using var linker = await LinkerReplayHarness.BuildLinkerAsync(
            [SynthGodzilla, SynthOktoberfest], extractor, MakeFixtureBlobStore());

        var raw = LinkerReplayHarness.MakeRaw(
            documentId: "doc_synth_wrong", fileUrl: "https://example.com/gz.pdf",
            gameSlug: string.Empty, manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage, localPath: "manualspage/gz.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Linked to Godzilla — which IS a mis-attribution against an expectation
        // of Oktoberfest. The policy check the live test applies:
        var expectedMachineId = "SYNTH-OK-01";
        var misattributed = result.FinalStatus == LinkStatus.Linked
            && !result.LinkedMachineIds.Contains(expectedMachineId, StringComparer.OrdinalIgnoreCase);
        Assert.True(misattributed, "harness failed to detect a planted mis-attribution");
    }

    [Fact]
    public async Task Synthetic_NoEvidence_FallsThroughWithoutLinking()
    {
        var extractor = new FixturePreviewExtractor(new Dictionary<string, IReadOnlyList<ExtractedPage>>
        {
            ["manualspage/blank.pdf"] = [new ExtractedPage(1, "24 VDC power supply wiring diagram")],
        });
        using var linker = await LinkerReplayHarness.BuildLinkerAsync(
            [SynthGodzilla], extractor, MakeFixtureBlobStore());

        var raw = LinkerReplayHarness.MakeRaw(
            documentId: "doc_synth_blank", fileUrl: "https://example.com/blank.pdf",
            gameSlug: string.Empty, manufacturerKey: "stern",
            sourceType: SourceType.ManualsPage, localPath: "manualspage/blank.pdf");

        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.NotEqual(LinkStatus.Linked, result.FinalStatus);
    }

    // ── Live-fixture replay (gated) ────────────────────────────────────────────

    [RequiresCapturedFixtureFact(
        CapturedFixtureRepoPath,
        "Run: dotnet run --project src/PinballWizard.Cli -c Release -- --capture-page-text " +
        "(see tests/PinballWizard.Application.Tests/Fixtures/Linking/CAPTURE-PAGE-TEXT.md)")]
    public async Task PageTextLinkSet_Replays_WithNoMisattribution()
    {
        var fixturePath = FixturePath();
        var fixture = JsonSerializer.Deserialize<PageTextLinkSetFixtureDto>(
                await File.ReadAllTextAsync(fixturePath), CaseInsensitiveOptions)
            ?? throw new InvalidOperationException($"Could not deserialize fixture at {fixturePath}.");
        Assert.NotEmpty(fixture.Entries);

        // Seed the catalog with the REAL captured machine titles — page-tier
        // resolution matches identity variants built from Machine.Title, so
        // slug-derived fake titles (the golden replay's shortcut) would not arm
        // these tiers.
        var machines = fixture.Entries
            .Where(e => e.ExpectedMachineId is { Length: > 0 } && e.ExpectedMachineTitle is { Length: > 0 })
            .GroupBy(e => e.ExpectedMachineId, StringComparer.OrdinalIgnoreCase)
            .Select(g => LinkerReplayHarness.MakeMachine(
                id: g.Key,
                manufacturerKey: g.First().ExpectedMachineManufacturer,
                title: g.First().ExpectedMachineTitle,
                slug: $"unused-{g.Key.ToLowerInvariant()}"))
            .ToList();

        var pagesByBlobName = fixture.Entries
            .GroupBy(e => e.LocalPath, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ExtractedPage>)g.First().PageTexts
                    .Select((text, i) => new ExtractedPage(i + 1, text)).ToList(),
                StringComparer.Ordinal);

        using var linker = await LinkerReplayHarness.BuildLinkerAsync(
            machines, new FixturePreviewExtractor(pagesByBlobName), MakeFixtureBlobStore());

        var mismatches = new List<string>();
        var notLinked = new List<string>();

        foreach (var entry in fixture.Entries)
        {
            var raw = LinkerReplayHarness.MakeRaw(
                documentId: entry.DocumentId,
                fileUrl: entry.FileUrl,
                gameSlug: entry.GameSlug ?? string.Empty,
                manufacturerKey: entry.ExpectedMachineManufacturer,
                docType: Enum.TryParse<DocumentType>(entry.DocumentType, out var dt) ? dt : DocumentType.Manual,
                sourceType: Enum.TryParse<SourceType>(entry.SourceType, out var st) ? st : SourceType.ManualsPage,
                localPath: entry.LocalPath);

            var result = await linker.LinkAsync(raw, CancellationToken.None);
            var resolved = result.LinkedMachineIds ?? [];

            if (result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked
                && !resolved.Contains(entry.ExpectedMachineId, StringComparer.OrdinalIgnoreCase))
            {
                mismatches.Add($"{entry.DocumentId}: expected {entry.ExpectedMachineId}, got [{string.Join(",", resolved)}]");
            }
            else if (result.FinalStatus is not (LinkStatus.Linked or LinkStatus.ManuallyLinked))
            {
                notLinked.Add($"{entry.DocumentId} ({result.FinalStatus})");
            }
        }

        if (notLinked.Count > 0)
        {
            Console.WriteLine($"[PageTextLinkSet] Entries that no longer link ({notLinked.Count}):");
            foreach (var nl in notLinked) Console.WriteLine($"  NOT_LINKED: {nl}");
        }

        // BOTH failure modes are blocking here — unlike the slug replay, this
        // fixture was captured WITH the evidence the tiers need, so an entry
        // that stops linking means the page tiers regressed, and an entry that
        // links elsewhere means mis-attribution.
        Assert.Empty(mismatches);
        Assert.Empty(notLinked);
    }

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
        return Path.Combine(root, CapturedFixtureRepoPath.Replace('/', Path.DirectorySeparatorChar));
    }

    // Read-side DTOs, parallel to CaptureGoldenSetCommand's write-side types —
    // duplicated deliberately so no CLI-project dependency is introduced (same
    // pattern as GoldenLinkSetFixtureDto).
    private sealed class PageTextLinkSetFixtureDto
    {
        public DateTimeOffset CapturedAt { get; init; }
        public string Source { get; init; } = string.Empty;
        public int DocumentCount { get; init; }
        public int EntryCount { get; init; }
        public List<PageTextLinkEntryDto> Entries { get; init; } = [];
    }

    private sealed class PageTextLinkEntryDto
    {
        public string DocumentId { get; init; } = string.Empty;
        public string LocalPath { get; init; } = string.Empty;
        public string FileUrl { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public string? GameSlug { get; init; }
        public string DocumentType { get; init; } = string.Empty;
        public string ResolutionStrategy { get; init; } = string.Empty;
        public string ExpectedMachineId { get; init; } = string.Empty;
        public string ExpectedMachineTitle { get; init; } = string.Empty;
        public string ExpectedMachineManufacturer { get; init; } = string.Empty;
        public List<string> PageTexts { get; init; } = [];
        public bool Truncated { get; init; }
    }
}
