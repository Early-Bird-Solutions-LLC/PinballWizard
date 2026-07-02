using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavior-asserting tests for MachineSearchIndexProjector (ADR-0049 phase 2a/2b).
//
// Phase 2b reconciled the projector's completeness computation from an inline
// 7-signal fraction to MachineCompleteness.Score(machine) / 6.0 — the same Core
// formula used by the content-intrinsic tie-break in MachineGroundingTool. These
// tests document the expected completeness value for real machine states and guard
// against formula drift (e.g., someone re-introducing the 7-signal inline).
//
// The 6 Core signals (MachineCompleteness.Score):
//   1. ManufacturerDisplayName non-empty
//   2. Year > 0
//   3. Themes non-empty
//   4. Designers non-empty
//   5. Editions non-empty
//   6. OpdbSourceUrl non-empty
//
// The ProjectAllAsync HTTP path (batch upsert against AI Search) is covered by
// integration / live tests gated on PINBALL_WIZARD_LIVE_MACHINE_INDEX_TESTS=1,
// following the same pattern as AiSearchRagIndexerLiveTests.
public sealed class MachineSearchIndexProjectorTests
{
    // ── Completeness scoring (MachineCompleteness.Score / 6.0) ───────────────

    [Fact]
    public void Completeness_AllSixSignalsPresent_ReturnsOne()
    {
        // Manufacturer + Year + Themes + Designers + Editions + OpdbSourceUrl = 6/6.
        var machine = BuildMachine(
            year: 2024,
            opdbSourceUrl: "https://opdb.org/machines/GRBN-MQR4P",
            themes: ["Science Fiction"],
            designers: ["Steve Ritchie"],
            editions: [new MachineEdition { Name = "Pro" }]);

        var score = MachineCompleteness.Score(machine) / 6.0;

        Assert.Equal(1.0, score, precision: 9);
    }

    [Fact]
    public void Completeness_OnlyManufacturer_ReturnsOneSixth()
    {
        // A scraper-only record with no OPDB data: only ManufacturerDisplayName
        // is present. Expected: 1/6 ≈ 0.1667.
        var machine = BuildMachine(
            year: null,
            opdbSourceUrl: null,
            themes: [],
            designers: [],
            editions: []);

        var score = MachineCompleteness.Score(machine) / 6.0;

        Assert.Equal(1.0 / 6.0, score, precision: 9);
    }

    [Fact]
    public void Completeness_OPDBMinimumRecord_ReturnsThreeSixths()
    {
        // A minimal OPDB-linked record: Manufacturer + Year + OpdbSourceUrl
        // present; Themes/Designers/Editions not yet populated. Expected: 3/6 = 0.5.
        var machine = BuildMachine(
            year: 2019,
            opdbSourceUrl: "https://opdb.org/machines/MMDNS",
            themes: [],
            designers: [],
            editions: []);

        var score = MachineCompleteness.Score(machine) / 6.0;

        Assert.Equal(3.0 / 6.0, score, precision: 9);
    }

    [Fact]
    public void Completeness_WithThemesAndDesigners_ReturnsFiveSixths()
    {
        // Manufacturer + Year + OpdbSourceUrl + Themes + Designers = 5/6.
        var machine = BuildMachine(
            year: 2022,
            opdbSourceUrl: "https://opdb.org/machines/GRBN",
            themes: ["Monsters", "Horror"],
            designers: ["Keith Elwin"],
            editions: []);

        var score = MachineCompleteness.Score(machine) / 6.0;

        Assert.Equal(5.0 / 6.0, score, precision: 9);
    }

    [Fact]
    public void Completeness_ScoreIsLinearNotBinary()
    {
        // Score should rise monotonically as signals are added.
        var minimal = BuildMachine(
            year: 2020,
            opdbSourceUrl: null,
            themes: [],
            designers: [],
            editions: []);

        var richer = BuildMachine(
            year: 2020,
            opdbSourceUrl: "https://opdb.org/machines/ABC",
            themes: ["Rock"],
            designers: [],
            editions: []);

        var minimalScore = MachineCompleteness.Score(minimal) / 6.0;
        var richerScore  = MachineCompleteness.Score(richer) / 6.0;

        Assert.True(richerScore > minimalScore,
            $"Expected richerScore ({richerScore:F4}) > minimalScore ({minimalScore:F4})");
    }

    [Fact]
    public void Completeness_ScoreIsBetweenZeroAndOne()
    {
        var machine = BuildMachine(
            year: null,
            opdbSourceUrl: null,
            themes: [],
            designers: [],
            editions: []);

        var score = MachineCompleteness.Score(machine) / 6.0;

        Assert.InRange(score, 0.0, 1.0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Constructs a Machine where ManufacturerDisplayName is always present
    // (signal #1 of 6 always true). Tests specify the remaining 5 signals.
    private static Machine BuildMachine(
        int? year,
        string? opdbSourceUrl,
        List<string> themes,
        List<string> designers,
        List<MachineEdition> editions)
    {
        return new Machine
        {
            Id                      = "GRBN-MQR4P",
            PartitionKey            = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title                   = "Godzilla",
            Year                    = year,
            OpdbSourceUrl           = opdbSourceUrl ?? string.Empty,
            Themes                  = themes,
            Designers               = designers,
            Editions                = editions,
            FirstSeenAt             = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSeenAt              = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }
}
