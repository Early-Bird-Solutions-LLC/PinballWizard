using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Application.Tests.Fixtures;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using System.Text.Json;
using Xunit;

namespace PinballWizard.Application.Tests.Sync;

// Regression gate for ADR-0054 Wave 2 — reconciler slug-matching algorithm.
//
// Two test tiers:
//
//   1. Synthetic-harness tests (always run): prove that the parity harness
//      detects a count drop when slug matching regresses. These run with no
//      live Cosmos dependency and prove the harness is correct BEFORE the
//      live-captured data is available.
//
//   2. Live-fixture replay (ReconcilerParity_Replays_WithNoSlugCountDrop):
//      reads Fixtures/Sync/reconciler-parity.captured.json. Skips explicitly
//      with an operator-runbook message when the file is absent. Run
//      `--capture-reconciler-parity` against a freshly synced Cosmos instance
//      to produce the file (see Fixtures/Sync/CAPTURE.md).
//
// Outcome policy (same in both tiers):
//   MatchedBySlug < captured count = BLOCKING (normalization regression)
//   MatchedBySlug >= captured count = PASS (exact or improvement)

public sealed class ReconcilerParityReplayTests
{
    // ── Shared JSON options ────────────────────────────────────────────────────

    // Cached so the analyzer (CA1869) doesn't flag a new instance per call.
    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    // ── Fixture file path ──────────────────────────────────────────────────────

    // ONE definition, shared by the [RequiresCapturedFixtureFact] decoration and the
    // test body — see the same const in GoldenLinkSetReplayTests. Two independent
    // spellings mean a typo in the attribute skips forever while the fixture sits
    // correctly on disk, and nothing ever reports it.
    internal const string CapturedFixtureRepoPath =
        "tests/PinballWizard.Application.Tests/Fixtures/Sync/reconciler-parity.captured.json";

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

    // ── Shared builder ─────────────────────────────────────────────────────────

    // Builds a ScraperReconciliationService with a fully mocked IMachineRepository
    // whose StreamByManufacturerAsync is stubbed to return the given machines per
    // manufacturer partition. UpsertAsync returns Task.CompletedTask (NSubstitute default).
    private static (ScraperReconciliationService Service, IMachineRepository Repo) BuildService(
        IReadOnlyDictionary<string, List<Machine>> byManufacturer)
    {
        var repo = Substitute.For<IMachineRepository>();
        var clock = new FixedClock(DateTimeOffset.UtcNow);

        foreach (var (manufacturer, machines) in byManufacturer)
        {
            repo.StreamByManufacturerAsync(manufacturer, Arg.Any<CancellationToken>())
                .Returns(ToAsync(machines));
        }

        var service = new ScraperReconciliationService(
            repo,
            clock,
            NullLogger<ScraperReconciliationService>.Instance);

        return (service, repo);
    }

    // Derives the GameId prefix for a manufacturer key, matching ScraperManufacturerKey.FromGameId
    // in reverse so we can reconstruct the GameId the reconciler expects. This is a parallel
    // table to the production switch in FromGameId — kept honest by
    // GameIdPrefix_RoundTripsThrough_RealFromGameId below, which asserts every entry here
    // actually round-trips through the PRODUCTION method. If the two drift, that canary fails
    // loudly instead of this table silently producing the wrong GameId for a new manufacturer.
    private static string GameIdPrefix(string manufacturerKey) => manufacturerKey switch
    {
        "stern"            => "game_",
        "jjp"              => "game_jjp_",
        "americanpinball"  => "game_ap_",
        "spooky"           => "game_spooky_",
        "pinballbrothers"  => "game_pinballbrothers_",
        "barrelsoffun"     => "game_barrelsoffun_",
        "cgc"              => "game_cgc_",
        "multimorphic"     => "game_multimorphic_",
        // Unknown manufacturer: fall back to bare "game_" and let the reconciler
        // derive "stern" from it. The test is conservative — a count miss is still
        // surfaced as a regression even when the prefix is wrong.
        _                  => "game_",
    };

    // Round-trips every entry in GameIdPrefix through the REAL ScraperManufacturerKey
    // .FromGameId. If someone adds a ninth manufacturer to FromGameId and forgets this table
    // (or edits one without the other), this fails immediately instead of GameIdPrefix silently
    // reconstructing the wrong GameId and the parity tests validating against a phantom slug.
    [Theory]
    [InlineData(ScraperManufacturerKey.Stern)]
    [InlineData(ScraperManufacturerKey.Jjp)]
    [InlineData(ScraperManufacturerKey.AmericanPinball)]
    [InlineData(ScraperManufacturerKey.Spooky)]
    [InlineData(ScraperManufacturerKey.PinballBrothers)]
    [InlineData(ScraperManufacturerKey.BarrelsOfFun)]
    [InlineData(ScraperManufacturerKey.ChicagoGaming)]
    [InlineData(ScraperManufacturerKey.Multimorphic)]
    public void GameIdPrefix_RoundTripsThrough_RealFromGameId(string manufacturerKey)
    {
        var gameId = GameIdPrefix(manufacturerKey) + "canary-slug";

        var resolved = ScraperManufacturerKey.FromGameId(gameId);

        Assert.Equal(manufacturerKey, resolved);
    }

    private static Machine MakeMachine(string id, string manufacturerKey, string title, string slug)
    {
        var m = new Machine
        {
            Id = id,
            PartitionKey = manufacturerKey,
            ManufacturerDisplayName = manufacturerKey,
            Title = title,
        };
        m.ManufacturerSlugs[manufacturerKey] = slug;
        return m;
    }

    private static async IAsyncEnumerable<Machine> ToAsync(IEnumerable<Machine> machines)
    {
        foreach (var m in machines)
        {
            yield return m;
            await Task.Yield();
        }
    }

    // ── Synthetic tests (always run) ──────────────────────────────────────────

    // Three Stern machines, each pre-seeded with a slug. The GameCatalog has
    // one entry per machine. ReconcileAsync must return MatchedBySlug == 3.
    private static readonly (string Id, string Slug, string Title)[] SynthMachines =
    [
        ("SYNTH-RECON01", "synth-alpha",   "Synth Alpha"),
        ("SYNTH-RECON02", "synth-beta",    "Synth Beta"),
        ("SYNTH-RECON03", "synth-gamma",   "Synth Gamma"),
    ];

    [Fact]
    public async Task SyntheticHarness_CorrectSlugs_AllMatchBySlug()
    {
        // Arrange: 3 machines with slugs pre-populated, 3 matching GameRecord entries.
        var machines = SynthMachines
            .Select(t => MakeMachine(t.Id, "stern", t.Title, t.Slug))
            .ToList();

        var byMfr = new Dictionary<string, List<Machine>>
        {
            ["stern"] = machines,
        };
        var (service, _) = BuildService(byMfr);

        var catalog = new GameCatalog
        {
            Games = SynthMachines
                .Select(t => new GameRecord
                {
                    GameId = $"game_{t.Slug}",
                    Title = t.Title,
                    Slug = t.Slug,
                    GamePageUrl = $"https://example.com/stern/game/{t.Slug}/",
                })
                .ToList(),
        };

        // Act
        var result = await service.ReconcileAsync(catalog, CancellationToken.None);

        // Assert: every machine slug fast-paths (no title fallback needed)
        Assert.Equal(3, result.MatchedBySlug);
        Assert.Equal(0, result.AmbiguousTitle);
        Assert.Equal(0, result.Unmatched);
    }

    [Fact]
    public async Task SyntheticHarness_BrokenNormalization_IsDetected()
    {
        // Arrange: deliberately break one slug so it no longer matches.
        // This proves the harness surfaces a count drop that signals a regression.
        // In practice, a normalization change (e.g. different whitespace stripping)
        // would cause the same count drop on the live-captured fixture.
        var machines = SynthMachines
            .Select(t => MakeMachine(t.Id, "stern", t.Title, t.Slug))
            .ToList();

        // Poison the first machine's slug — the slug that was "synth-alpha" is now
        // "SYNTH-ALPHA-BROKEN" which the GameCatalog entry "synth-alpha" cannot match.
        machines[0].ManufacturerSlugs["stern"] = "SYNTH-ALPHA-BROKEN";

        var byMfr = new Dictionary<string, List<Machine>>
        {
            ["stern"] = machines,
        };
        var (service, _) = BuildService(byMfr);

        var catalog = new GameCatalog
        {
            Games = SynthMachines
                .Select(t => new GameRecord
                {
                    GameId = $"game_{t.Slug}",
                    Title = t.Title,
                    Slug = t.Slug,
                    GamePageUrl = $"https://example.com/stern/game/{t.Slug}/",
                })
                .ToList(),
        };

        // Act
        var result = await service.ReconcileAsync(catalog, CancellationToken.None);

        // Assert: only 2 of 3 matched by slug — the harness detects the drop
        Assert.Equal(2, result.MatchedBySlug);
        // The parity gate compares to 3 (captured) → if this were a live test
        // with capturedSlugCount = 3, it would FAIL (regression detected).
        Assert.True(
            result.MatchedBySlug < SynthMachines.Length,
            "Broken normalization must produce fewer MatchedBySlug than the total seeded — " +
            "proving the harness detects a slug-count drop.");
    }

    [Fact]
    public async Task SyntheticHarness_AllThreeOutcomes_SlugAndTitleAndUnmatched()
    {
        // Arrange: machine A has a slug (slug fast path), machine B has no slug
        // (title fallback), machine C has no slug and is ambiguous (unmatched).
        var machineA = MakeMachine("SYNTH-RA1", "stern", "Synth Alpha", "synth-alpha");
        var machineB = new Machine
        {
            Id = "SYNTH-RB2",
            PartitionKey = "stern",
            ManufacturerDisplayName = "stern",
            Title = "Synth Beta",
        };
        // Machines C and D share the same title (ambiguous)
        var machineC = new Machine
        {
            Id = "SYNTH-RC3",
            PartitionKey = "stern",
            ManufacturerDisplayName = "stern",
            Title = "Synth Gamma",
        };
        var machineD = new Machine
        {
            Id = "SYNTH-RD4",
            PartitionKey = "stern",
            ManufacturerDisplayName = "stern",
            Title = "Synth Gamma",
        };

        var byMfr = new Dictionary<string, List<Machine>>
        {
            ["stern"] = [machineA, machineB, machineC, machineD],
        };
        var (service, _) = BuildService(byMfr);

        var catalog = new GameCatalog
        {
            Games =
            [
                new() { GameId = "game_synth-alpha", Title = "Synth Alpha", Slug = "synth-alpha", GamePageUrl = "https://x/a" },
                new() { GameId = "game_synth-beta",  Title = "Synth Beta",  Slug = "synth-beta",  GamePageUrl = "https://x/b" },
                new() { GameId = "game_synth-gamma", Title = "Synth Gamma", Slug = "synth-gamma", GamePageUrl = "https://x/c" },
            ],
        };

        // Act
        var result = await service.ReconcileAsync(catalog, CancellationToken.None);

        // A matched by slug, B by title, C/D ambiguous
        Assert.Equal(1, result.MatchedBySlug);
        Assert.Equal(1, result.MatchedByTitle);
        Assert.Equal(1, result.AmbiguousTitle);
    }

    // ── Live-fixture replay ────────────────────────────────────────────────────

    // RequiresCapturedFixtureFact skips this test at discovery time when the fixture is
    // absent — the skip message names the capture command so a reader can never mistake
    // "Skipped" for "Passed". Once the operator runs --capture-reconciler-parity and the
    // file lands, the attribute stops skipping and the test runs for real.
    [RequiresCapturedFixtureFact(
        CapturedFixtureRepoPath,
        "Run: dotnet run --project src/PinballWizard.Cli -c Release -- --capture-reconciler-parity " +
        "(see tests/PinballWizard.Application.Tests/Fixtures/Sync/CAPTURE.md)")]
    public async Task ReconcilerParity_Replays_WithNoSlugCountDrop()
    {
        var fixturePath = CapturedFixturePath();

        var json = await File.ReadAllTextAsync(fixturePath);
        var fixture = JsonSerializer.Deserialize<ReconcilerParityFixtureDto>(json, CaseInsensitiveOptions)
            ?? throw new InvalidOperationException($"Could not deserialize fixture at {fixturePath}.");

        Assert.NotEmpty(fixture.Entries);

        // Group captured slug entries by manufacturer partition.
        var byManufacturer = fixture.Entries
            .GroupBy(e => e.ManufacturerKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => MakeMachine(e.MachineId, e.ManufacturerKey, e.Title, e.Slug)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var (service, _) = BuildService(byManufacturer);

        // Build a GameCatalog entry for each slug entry. One GameRecord per slug
        // so each slug fast-paths through MatchedBySlug exactly once.
        var games = fixture.Entries
            .Select(e => new GameRecord
            {
                GameId = $"{GameIdPrefix(e.ManufacturerKey)}{e.Slug}",
                Title = e.Title,
                Slug = e.Slug,
                GamePageUrl = $"https://example.com/{e.ManufacturerKey}/game/{e.Slug}/",
            })
            .DistinctBy(g => g.GameId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var catalog = new GameCatalog { Games = games };

        // Act
        var result = await service.ReconcileAsync(catalog, CancellationToken.None);

        var capturedSlugCount = fixture.Entries.Count;

        // A drop in MatchedBySlug means a normalization change broke slug resolution
        // for machines that previously resolved. This is the regression the gate exists
        // to detect.
        Assert.True(
            result.MatchedBySlug >= capturedSlugCount,
            $"Reconciler slug-count regression: expected >= {capturedSlugCount} MatchedBySlug " +
            $"(captured total) but got {result.MatchedBySlug}. " +
            $"This means a normalization or key-lookup change broke slug resolution for " +
            $"{capturedSlugCount - result.MatchedBySlug} machine(s). " +
            $"Check ScraperManufacturerKey, ScraperReconciliationService.FindMatch, and " +
            $"LinkingUtilities.NormalizeForMatch for regressions.");

        Console.WriteLine(
            $"[ReconcilerParity] MatchedBySlug={result.MatchedBySlug} " +
            $"(captured={capturedSlugCount}, delta={result.MatchedBySlug - capturedSlugCount:+#;-#;0})");
    }

    // ── Fixture DTO types (read-side) ──────────────────────────────────────────

    private sealed class ReconcilerParityFixtureDto
    {
        public DateTimeOffset CapturedAt { get; init; }
        public string Source { get; init; } = string.Empty;
        public int TotalMachines { get; init; }
        public int TotalSlugged { get; init; }
        public Dictionary<string, ManufacturerSlugStatDto> ManufacturerStats { get; init; } = [];
        public List<ReconcilerParityEntryDto> Entries { get; init; } = [];
    }

    private sealed class ReconcilerParityEntryDto
    {
        public string MachineId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string ManufacturerKey { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
    }

    private sealed class ManufacturerSlugStatDto
    {
        public int MachineCount { get; init; }
        public int SluggedCount { get; init; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
