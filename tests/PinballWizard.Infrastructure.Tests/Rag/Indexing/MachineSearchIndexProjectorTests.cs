using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavior-asserting tests for MachineSearchIndexProjector (ADR-0049 phase 2a).
// ComputeCompleteness is internal static — testable without a live SearchClient.
// The ProjectAllAsync HTTP path (batch upsert against AI Search) is covered by
// integration / live tests gated on PINBALL_WIZARD_LIVE_MACHINE_INDEX_TESTS=1,
// following the same pattern as AiSearchRagIndexerLiveTests.
public sealed class MachineSearchIndexProjectorTests
{
    // ── Completeness scoring ──────────────────────────────────────────────────

    [Fact]
    public void ComputeCompleteness_AllSignalsPresent_ReturnsOne()
    {
        // A fully-populated machine record (title + year + manufacturer +
        // groupId + themes + designers + editions) should score 1.0.
        var machine = BuildMachine(
            year: 2024,
            groupId: "GweeP",
            themes: ["Science Fiction"],
            designers: ["Steve Ritchie"],
            editions: [new MachineEdition { Name = "Pro" }]);

        var score = MachineSearchIndexProjector.ComputeCompleteness(machine);

        Assert.Equal(1.0, score, precision: 9);
    }

    [Fact]
    public void ComputeCompleteness_OnlyTitleAndManufacturer_ReturnsTwoSevenths()
    {
        // A scraper-only record with no OPDB data: title + manufacturer present
        // (signals 1+3 of 7). Expected: 2/7 ≈ 0.2857.
        var machine = BuildMachine(
            year: null,
            groupId: null,
            themes: [],
            designers: [],
            editions: []);

        var score = MachineSearchIndexProjector.ComputeCompleteness(machine);

        Assert.Equal(2.0 / 7.0, score, precision: 9);
    }

    [Fact]
    public void ComputeCompleteness_OPDBMinimumRecord_ReturnsFourSevenths()
    {
        // A minimal but valid OPDB-linked record has title + year + manufacturer +
        // groupId (signals 1+2+3+4 of 7). Expected: 4/7 ≈ 0.5714.
        // This is the practical floor for OPDB records per ADR-0049 comment.
        var machine = BuildMachine(
            year: 2019,
            groupId: "MMDNS",
            themes: [],
            designers: [],
            editions: []);

        var score = MachineSearchIndexProjector.ComputeCompleteness(machine);

        Assert.Equal(4.0 / 7.0, score, precision: 9);
    }

    [Fact]
    public void ComputeCompleteness_WithThemesButNoDesignersOrEditions_ReturnsFiveSevenths()
    {
        var machine = BuildMachine(
            year: 2022,
            groupId: "GRBN",
            themes: ["Monsters", "Horror"],
            designers: [],
            editions: []);

        var score = MachineSearchIndexProjector.ComputeCompleteness(machine);

        Assert.Equal(5.0 / 7.0, score, precision: 9);
    }

    [Fact]
    public void ComputeCompleteness_AllListsPopulatedButMissingYearAndGroup_ReturnsFiveSevenths()
    {
        // title + manufacturer + themes + designers + editions = 5/7
        var machine = BuildMachine(
            year: null,
            groupId: null,
            themes: ["Comedy"],
            designers: ["Pat Lawlor"],
            editions: [new MachineEdition { Name = "Standard" }]);

        var score = MachineSearchIndexProjector.ComputeCompleteness(machine);

        Assert.Equal(5.0 / 7.0, score, precision: 9);
    }

    // ── Document field mapping ────────────────────────────────────────────────

    [Fact]
    public void ComputeCompleteness_IsLinear_NotBinary()
    {
        // Score scales with the number of present signals, not boolean.
        var partial = BuildMachine(
            year: 2020,
            groupId: null,
            themes: [],
            designers: [],
            editions: []);

        var fuller = BuildMachine(
            year: 2020,
            groupId: "ABCD",
            themes: [],
            designers: [],
            editions: []);

        var partialScore = MachineSearchIndexProjector.ComputeCompleteness(partial);
        var fullerScore  = MachineSearchIndexProjector.ComputeCompleteness(fuller);

        Assert.True(fullerScore > partialScore,
            $"Expected fullerScore ({fullerScore:F4}) > partialScore ({partialScore:F4})");
    }

    [Fact]
    public void ComputeCompleteness_ScoreIsBetweenZeroAndOne()
    {
        var machine = BuildMachine(
            year: null,
            groupId: null,
            themes: [],
            designers: [],
            editions: []);

        var score = MachineSearchIndexProjector.ComputeCompleteness(machine);

        Assert.InRange(score, 0.0, 1.0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Constructs a Machine where title + manufacturer are always present
    // (signals 1+3 of 7 always true). Tests specify the remaining signals.
    private static Machine BuildMachine(
        int? year,
        string? groupId,
        List<string> themes,
        List<string> designers,
        List<MachineEdition> editions)
    {
        return new Machine
        {
            Id                      = "GRBN-MQR4P",
            PartitionKey            = "stern",
            ManufacturerDisplayName  = "Stern Pinball",
            Title                   = "Godzilla",
            Year                    = year,
            GroupId                 = groupId,
            Themes                  = themes,
            Designers               = designers,
            Editions                = editions,
            FirstSeenAt             = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSeenAt              = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }
}
