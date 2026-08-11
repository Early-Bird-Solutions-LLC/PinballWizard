using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;
using PinballWizard.Application.Tests.Fixtures;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using System.Text.Json;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// Regression gate for ADR-0054 Wave 2 (Plan 2 consumer migrations).
//
// Two test tiers:
//
//   1. Synthetic-harness tests (always run): prove the replay machinery detects
//      mis-attribution, reports needs_review without failing, and reports a
//      not_in_catalog→linked win without failing. These prove the harness is
//      correct BEFORE it is ever fed the live-captured data.
//
//   2. Live-fixture replay (GoldenLinkSet_Replays_WithNoMisattribution): reads
//      Fixtures/Linking/golden-link-set.captured.json. Skips explicitly with an
//      operator-runbook message when the file is absent — "skipped, never captured"
//      must never look like "passed". Run `--capture-golden-set` against the
//      re-linked corpus to produce the file (see Fixtures/Linking/CAPTURE.md).
//
// Outcome policy (same in both tiers):
//   linked → different machine  = BLOCKING (mis-attribution — provenance is sacred)
//   linked → needs_review       = report; do NOT fail
//   not_in_catalog → linked     = WIN; report; do NOT fail

public sealed class GoldenLinkSetReplayTests
{
    // ── Shared JSON options ────────────────────────────────────────────────────

    // Cached so the analyzer (CA1869) doesn't flag a new instance per call.
    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    // ── Fixture file path ──────────────────────────────────────────────────────

    // ONE definition of the fixture path, shared by the [RequiresCapturedFixtureFact]
    // decoration and the test body. Previously each spelled the path independently: a
    // typo in the attribute would set Skip forever while the fixture sat correctly on
    // disk, and nothing would ever report it — a permanently green-looking gate that
    // never runs. Referencing the same const makes that divergence unrepresentable.
    internal const string CapturedFixtureRepoPath =
        "tests/PinballWizard.Application.Tests/Fixtures/Linking/golden-link-set.captured.json";

    // Path resolved from the test assembly root — walks up to find PinballWizard.slnx
    // then navigates into tests/. Must survive both `dotnet test` from the repo root
    // and IDE test runners that set a different working directory.
    private static string CapturedFixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
        return Path.Combine(
            root, CapturedFixtureRepoPath.Replace('/', Path.DirectorySeparatorChar));
    }

    // ── Shared linker builder ──────────────────────────────────────────────────

    // Builds a DocumentLinker seeded with the given machines so Tier-1 (game_slug)
    // can match. No text extractor or blob store — Tiers 3-4 are skipped, which is
    // exactly what the CAPTURE.md describes: "the mock catalog only seeds
    // slug-resolvable machines, so filename/page-text tiers return nothing."
    private static async Task<DocumentLinker> BuildLinkerAsync(
        IEnumerable<Machine> machines,
        CancellationToken ct = default)
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());

        var machineList = machines.ToList();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        // FanOutAndUpdateAsync calls UpdateLinkStatusAsync on rawRepo and UpsertFromRawAsync
        // on docWriter. NSubstitute defaults (Task.CompletedTask for Task returns) are fine.
        // PruneStaleFanOutRowsAsync calls StreamByDocumentIdAsync — NSubstitute 5.x returns
        // an empty async enumerable by default; any NullRef is caught inside the try/catch
        // in PruneStaleFanOutRowsAsync, so the link result is never corrupted.

        // The resolver is mandatory since ADR-0054 Wave 2 Task 8 — the replay runs
        // the identity-derived index with an empty curated-alias list.
        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            textExtractor: null,
            NullLogger<DocumentLinker>.Instance,
            aliasLoader,
            blobStore: null);

        await linker.InitializeAsync(ct);
        return linker;
    }

    // Builds a minimal RawDocumentRecord whose Tier-1 slug resolves via raw.Game.Slug.
    private static RawDocumentRecord MakeRaw(
        string documentId,
        string fileUrl,
        string gameSlug,
        string manufacturerKey,
        DocumentType docType = DocumentType.Manual,
        // The captured source_type must be replayed faithfully. LinkingUtilities
        // .InferManufacturerKey derives the manufacturer hint FROM SourceType
        // (ManualsPage => Stern), and that hint drives the resolver's manufacturer
        // scoping. Hardcoding
        // ManualsPage would stamp every replayed document "stern" regardless of its real
        // manufacturer, so a non-Stern document whose slug collides with a Stern machine
        // would fail the gate as a mis-attribution the linker never made.
        SourceType sourceType = SourceType.ManualsPage)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = docType,
            Source = new SourceInfo
            {
                DiscoveryUrl = $"https://example.com/{manufacturerKey}/manuals/",
                DiscoveryContext = $"{manufacturerKey} Manuals page",
                FileUrl = fileUrl,
                ScrapedAt = DateTime.UtcNow,
                SourceType = sourceType,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            Game = new GameReference
            {
                Title = gameSlug.Replace('-', ' '),
                Slug = gameSlug,
                GamePageUrl = $"https://example.com/{manufacturerKey}/game/{gameSlug}/",
            },
        };

    // Builds a Machine where ManufacturerSlugs[manufacturerKey] = slug.
    private static Machine MakeMachine(string id, string manufacturerKey, string title, string slug)
        => new()
        {
            Id = id,
            PartitionKey = manufacturerKey,
            ManufacturerDisplayName = manufacturerKey,
            Title = title,
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [manufacturerKey] = slug,
            },
        };

    // Runs the replay policy against one entry and returns (misattribution:bool, needsReview:bool, win:bool).
    // The policy mirrors GoldenLinkSet_Replays_WithNoMisattribution exactly, so any divergence in
    // the policy also shows up in the synthetic tests.
    // ResolvedIds is returned so a failing gate can NAME the machine the linker actually
    // chose. Without it the mismatch message can only restate what was expected, which is
    // useless for triage — the whole point of the gate is to identify where a document
    // went instead.
    private static async Task<(bool IsMisattribution, bool IsNeedsReview, bool IsWin, IReadOnlyList<string> ResolvedIds)> ReplayEntryAsync(
        DocumentLinker linker,
        GoldenLinkEntryDto entry,
        CancellationToken ct = default)
    {
        var raw = MakeRaw(
            documentId: entry.DocumentId,
            fileUrl: entry.FileUrl,
            gameSlug: entry.GameSlug ?? string.Empty,
            manufacturerKey: entry.ManufacturerKey ?? string.Empty,
            docType: Enum.TryParse<DocumentType>(entry.DocumentType, out var dt) ? dt : DocumentType.Manual,
            sourceType: Enum.TryParse<SourceType>(entry.SourceType, out var st) ? st : SourceType.ManualsPage);

        var result = await linker.LinkAsync(raw, ct);
        var resolved = result.LinkedMachineIds ?? [];

        // linked → different machine: the linker produced a machine ID that is NOT the
        // expected one. This is a mis-attribution and must cause the test to fail.
        if (result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked
            && !resolved.Contains(entry.ExpectedMachineId, StringComparer.OrdinalIgnoreCase))
        {
            return (IsMisattribution: true, IsNeedsReview: false, IsWin: false, ResolvedIds: resolved);
        }

        // linked → needs_review: the linker could not resolve the document. Report but
        // do NOT fail — the mock catalog only seeds slug-resolvable machines.
        if (result.FinalStatus is LinkStatus.NotInCatalog or LinkStatus.Failed)
        {
            return (IsMisattribution: false, IsNeedsReview: true, IsWin: false, ResolvedIds: resolved);
        }

        // not_in_catalog → linked: would occur if entry captured with status
        // not_in_catalog but the linker now resolves it (future extension). WIN.
        if (result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked
            && entry.ExpectedMachineId == NotInCatalogSentinel)
        {
            return (IsMisattribution: false, IsNeedsReview: false, IsWin: true, ResolvedIds: resolved);
        }

        return (IsMisattribution: false, IsNeedsReview: false, IsWin: false, ResolvedIds: resolved);
    }

    // Sentinel used to mark "was not_in_catalog at capture time" in the fixture.
    // If a live fixture ever includes these and the linker resolves them → WIN.
    private const string NotInCatalogSentinel = "__not_in_catalog__";

    // ── Synthetic tests (always run — prove the harness before live data exists) ──

    // Three synthetic machines / entries — the simplest catalog that exercises all
    // three outcome branches. Clearly marked as synthetic so no reader mistakes them
    // for live-captured data.
    private static readonly Machine SyntheticMachineA = MakeMachine(
        id: "SYNTH-AABBCC01",
        manufacturerKey: "stern",
        title: "Synth Alpha",
        slug: "synth-alpha");

    private static readonly Machine SyntheticMachineB = MakeMachine(
        id: "SYNTH-DDEEFF02",
        manufacturerKey: "stern",
        title: "Synth Beta",
        slug: "synth-beta");

    // SyntheticMachineC is intentionally NOT added to the linker catalog, so
    // documents linking to "synth-gamma" always return NotInCatalog (needs_review).

    // Two machines from DIFFERENT manufacturers sharing the same slug string.
    // Slug variants are keyed by slug text alone across the whole catalog, so these
    // collide into one candidate set and the resolver's manufacturer scoping must pick
    // the right one using the manufacturer hint DERIVED FROM SourceType.
    private static readonly Machine SyntheticSternShared = MakeMachine(
        id: "SYNTH-STERN03",
        manufacturerKey: "stern",
        title: "Synth Shared (Stern)",
        slug: "synth-shared");

    private static readonly Machine SyntheticJjpShared = MakeMachine(
        id: "SYNTH-JJP04",
        manufacturerKey: "jjp",
        title: "Synth Shared (JJP)",
        slug: "synth-shared");

    [Fact]
    public async Task SyntheticHarness_SourceType_DrivesManufacturerHint_ForSlugCollision()
    {
        // Two manufacturers share a slug. If MakeRaw ignored entry.SourceType (the
        // regression the local review found), every replayed document would carry
        // the ManualsPage->stern hint regardless of its real source, so this JJP
        // document would narrow to the Stern candidate and the gate would report a
        // mis-attribution the linker never actually made — a false positive baked
        // into the harness itself, not a real regression in Wave 2.
        var linker = await BuildLinkerAsync([SyntheticSternShared, SyntheticJjpShared]);
        var entry = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth005",
            FileUrl = "https://example.com/jjp/synth-shared-manual.pdf",
            SourceType = "JjpProductPage",
            GameSlug = "synth-shared",
            DocumentType = "Manual",
            ManufacturerKey = "jjp",
            ExpectedMachineId = SyntheticJjpShared.Id,
        };

        var (isMisattribution, _, _, resolvedIds) = await ReplayEntryAsync(linker, entry);

        Assert.False(isMisattribution,
            $"SourceType=JjpProductPage must hint manufacturer=jjp so the slug collision " +
            $"resolves to {SyntheticJjpShared.Id}, not the Stern sibling. Resolved: [{string.Join(",", resolvedIds)}]");
    }

    [Fact]
    public async Task SyntheticHarness_CorrectBinding_PassesQuietly()
    {
        // Arrange: a document whose game_slug resolves to SyntheticMachineA.
        // The fixture says expectedMachineId = SyntheticMachineA.Id — should be a clean pass.
        var linker = await BuildLinkerAsync([SyntheticMachineA, SyntheticMachineB]);
        var entry = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth001",
            FileUrl = "https://example.com/stern/synth-alpha-manual.pdf",
            SourceType = "ManualsPage",
            GameSlug = "synth-alpha",
            DocumentType = "Manual",
            ManufacturerKey = "stern",
            ExpectedMachineId = SyntheticMachineA.Id,
        };

        // Act
        var (isMisattribution, isNeedsReview, isWin, _) = await ReplayEntryAsync(linker, entry);

        // Assert: no mis-attribution, no needs_review — clean pass
        Assert.False(isMisattribution,
            $"Should not flag mis-attribution when linker resolves to the expected machine.");
        Assert.False(isNeedsReview,
            $"Should not flag needs_review when the machine is in the catalog.");
    }

    [Fact]
    public async Task SyntheticHarness_Misattribution_IsDetected()
    {
        // Arrange: entry says expectedMachineId = MachineA, but the game_slug
        // resolves to MachineB. This is the mis-attribution the replay gate exists to catch.
        var linker = await BuildLinkerAsync([SyntheticMachineA, SyntheticMachineB]);
        var entry = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth002",
            FileUrl = "https://example.com/stern/synth-beta-manual.pdf",
            SourceType = "ManualsPage",
            // slug resolves to MachineB …
            GameSlug = "synth-beta",
            DocumentType = "Manual",
            ManufacturerKey = "stern",
            // … but fixture claims MachineA — deliberate mis-attribution
            ExpectedMachineId = SyntheticMachineA.Id,
        };

        // Act
        var (isMisattribution, _, _, _) = await ReplayEntryAsync(linker, entry);

        // Assert: the harness correctly identifies the mis-attribution
        Assert.True(isMisattribution,
            $"Replay harness must detect mis-attribution: slug 'synth-beta' resolves to {SyntheticMachineB.Id} " +
            $"but fixture expects {SyntheticMachineA.Id}.");
    }

    [Fact]
    public async Task SyntheticHarness_NeedsReview_IsReportedNotFailed()
    {
        // Arrange: a document whose game_slug is NOT in the catalog — the linker
        // returns NotInCatalog. The harness must report this but NOT fail (the
        // mock catalog only seeds slug-resolvable machines; some docs may legitimately
        // resolve only via page-text tiers that the offline replay can't exercise).
        var linker = await BuildLinkerAsync([SyntheticMachineA, SyntheticMachineB]);
        // "synth-gamma" is not in the catalog
        var entry = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth003",
            FileUrl = "https://example.com/stern/synth-gamma-manual.pdf",
            SourceType = "ManualsPage",
            GameSlug = "synth-gamma",
            DocumentType = "Manual",
            ManufacturerKey = "stern",
            ExpectedMachineId = "SYNTH-GAMMA99",
        };

        // Act
        var (isMisattribution, isNeedsReview, _, _) = await ReplayEntryAsync(linker, entry);

        // Assert: not a mis-attribution (linker returned no machine, not a different one)
        // but classified as needs_review
        Assert.False(isMisattribution,
            "A NotInCatalog result is NOT a mis-attribution — the linker returned no machine at all.");
        Assert.True(isNeedsReview,
            "A NotInCatalog result must be classified as needs_review so it shows up in the report.");
    }

    [Fact]
    public async Task SyntheticHarness_AllThreeEntries_ProducesCorrectOutcomeCounts()
    {
        // Integration of all three outcomes in one pass — this mirrors the structure of
        // the live-fixture replay so the policy logic stays in sync between the two tiers.
        var linker = await BuildLinkerAsync([SyntheticMachineA, SyntheticMachineB]);

        // Entry 1: correct binding (should pass)
        var entry1 = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth_batch1",
            FileUrl = "https://example.com/stern/synth-alpha-rules.pdf",
            SourceType = "ManualsPage",
            GameSlug = "synth-alpha",
            DocumentType = "Manual",
            ManufacturerKey = "stern",
            ExpectedMachineId = SyntheticMachineA.Id,
        };

        // Entry 2: needs_review (slug not in catalog, should report, not fail)
        var entry2 = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth_batch2",
            FileUrl = "https://example.com/stern/synth-delta-rules.pdf",
            SourceType = "ManualsPage",
            GameSlug = "synth-delta",
            DocumentType = "Manual",
            ManufacturerKey = "stern",
            ExpectedMachineId = "SYNTH-DELTA99",
        };

        // Entry 3: mis-attribution (slug resolves to MachineB, fixture claims MachineA)
        var entry3 = new GoldenLinkEntryDto
        {
            DocumentId = "doc_synth_batch3",
            FileUrl = "https://example.com/stern/synth-beta-rules.pdf",
            SourceType = "ManualsPage",
            GameSlug = "synth-beta",
            DocumentType = "Manual",
            ManufacturerKey = "stern",
            ExpectedMachineId = SyntheticMachineA.Id, // wrong — should be MachineB
        };

        var mismatches = new List<string>();
        var needsReview = new List<string>();

        foreach (var entry in new[] { entry1, entry2, entry3 })
        {
            var (isMisattribution, isNR, _, _) = await ReplayEntryAsync(linker, entry);
            if (isMisattribution)
                mismatches.Add(entry.DocumentId);
            if (isNR)
                needsReview.Add(entry.DocumentId);
        }

        // Entry 1 passes → no mis-attribution for it; entry 3 is a mis-attribution
        Assert.Single(mismatches);
        Assert.Equal("doc_synth_batch3", mismatches[0]);

        // Entry 2 is needs_review; entries 1 and 3 are not
        Assert.Single(needsReview);
        Assert.Equal("doc_synth_batch2", needsReview[0]);
    }

    // ── Live-fixture replay ────────────────────────────────────────────────────

    // GoldenLinkSet_Replays_WithNoMisattribution is the actual Wave-2 regression gate.
    // RequiresCapturedFixtureFact skips this test at discovery time when the fixture is
    // absent — the skip message names the capture command so a reader can never mistake
    // "Skipped" for "Passed". Once the operator runs --capture-golden-set and the file
    // lands, the attribute stops skipping and the test runs for real.
    [RequiresCapturedFixtureFact(
        CapturedFixtureRepoPath,
        "Run: dotnet run --project src/PinballWizard.Cli -c Release -- --capture-golden-set " +
        "(see tests/PinballWizard.Application.Tests/Fixtures/Linking/CAPTURE.md)")]
    public async Task GoldenLinkSet_Replays_WithNoMisattribution()
    {
        var fixturePath = CapturedFixturePath();

        var json = await File.ReadAllTextAsync(fixturePath);
        var fixture = JsonSerializer.Deserialize<GoldenLinkSetFixtureDto>(json, CaseInsensitiveOptions)
            ?? throw new InvalidOperationException($"Could not deserialize fixture at {fixturePath}.");

        Assert.NotEmpty(fixture.Entries);

        // Seed the linker with one machine per unique expected machine ID in the fixture.
        // ManufacturerSlugs is set to [manufacturerKey → gameSlug] so Tier-1 game_slug
        // matching can resolve documents. Machines without a slug entry in the fixture
        // are seeded with an empty slug map — they fall through to needs_review.
        var machineMap = fixture.Entries
            .Where(e => e.GameSlug is { Length: > 0 } && e.ManufacturerKey is { Length: > 0 })
            .GroupBy(e => e.ExpectedMachineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (Slug: g.First().GameSlug!, ManufacturerKey: g.First().ManufacturerKey!),
                StringComparer.OrdinalIgnoreCase);

        var machines = machineMap
            .Select(kv => MakeMachine(
                id: kv.Key,
                manufacturerKey: kv.Value.ManufacturerKey,
                title: kv.Value.Slug.Replace('-', ' '),
                slug: kv.Value.Slug))
            .ToList();

        var linker = await BuildLinkerAsync(machines);

        var mismatches = new List<string>();
        var needsReview = new List<string>();
        var wins = new List<string>();

        foreach (var entry in fixture.Entries)
        {
            var (isMisattribution, isNR, isWin, resolvedIds) = await ReplayEntryAsync(linker, entry);
            if (isMisattribution)
                mismatches.Add($"{entry.DocumentId}: expected {entry.ExpectedMachineId}, " +
                               $"got [{string.Join(",", resolvedIds)}]");
            if (isNR)
                needsReview.Add(entry.DocumentId);
            if (isWin)
                wins.Add(entry.DocumentId);
        }

        // Report wins and needs_review so they appear in the test output.
        if (wins.Count > 0)
        {
            Console.WriteLine($"[GoldenLinkSet] Wins (not_in_catalog → linked): {wins.Count}");
            foreach (var w in wins)
                Console.WriteLine($"  WIN: {w}");
        }

        if (needsReview.Count > 0)
        {
            Console.WriteLine($"[GoldenLinkSet] Needs review (linked → no machine in mock catalog): {needsReview.Count}");
            foreach (var nr in needsReview)
                Console.WriteLine($"  NEEDS_REVIEW: {nr}");
        }

        // Mis-attributions are BLOCKING — they mean the linker now resolves a document
        // to a DIFFERENT machine than it did when the golden set was captured.
        Assert.Empty(mismatches);
    }

    // ── Fixture DTO types (read-side, parallel to CaptureGoldenSetCommand's types) ──
    // Defined here so no cross-project dependency is introduced to the CLI project.

    private sealed class GoldenLinkSetFixtureDto
    {
        public DateTimeOffset CapturedAt { get; init; }
        public string Source { get; init; } = string.Empty;
        public int DocumentCount { get; init; }
        public int EntryCount { get; init; }
        public List<GoldenLinkEntryDto> Entries { get; init; } = [];
    }

    private sealed class GoldenLinkEntryDto
    {
        public string DocumentId { get; init; } = string.Empty;
        public string FileUrl { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public string? GameSlug { get; init; }
        public string DocumentType { get; init; } = string.Empty;
        public string? ManufacturerKey { get; init; }
        public string ExpectedMachineId { get; init; } = string.Empty;
    }
}
