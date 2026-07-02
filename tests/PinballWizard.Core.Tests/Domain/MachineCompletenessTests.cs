using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Core.Tests.Domain;

public sealed class MachineCompletenessTests
{
    // Helpers

    private static Machine Bare() => new()
    {
        Id = "TEST-001",
        PartitionKey = "stern",
        ManufacturerDisplayName = "", // empty — not scored
        Title = "Test Machine",
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static Machine Rich() => new()
    {
        Id = "TEST-002",
        PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball",
        Title = "Foo Fighters",
        Year = 2023,
        Themes = ["rock", "music"],
        Designers = ["George Gomez"],
        Editions = [new MachineEdition { Name = "Pro" }],
        OpdbSourceUrl = "https://opdb.org/machines/TEST-002",
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    // ── Boundary: all fields absent ───────────────────────────────────────

    [Fact]
    public void Score_NoFields_ReturnsZero()
    {
        // A machine with no populated optional fields should score 0.
        // ManufacturerDisplayName is required but empty in Bare() to test
        // the lower bound.
        var machine = Bare();
        Assert.Equal(0, MachineCompleteness.Score(machine));
    }

    // ── Individual field contributions ────────────────────────────────────

    [Fact]
    public void Score_YearSet_AddsOne()
    {
        var machine = Bare();
        machine.Year = 2023;
        Assert.Equal(1, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_YearZero_DoesNotAdd()
    {
        // Year = 0 means unknown — should not contribute a point.
        var machine = Bare();
        machine.Year = 0;
        Assert.Equal(0, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_ThemesNonEmpty_AddsOne()
    {
        var machine = Bare();
        machine.Themes = ["horror"];
        Assert.Equal(1, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_ThemesEmpty_DoesNotAdd()
    {
        var machine = Bare();
        machine.Themes = [];
        Assert.Equal(0, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_DesignersNonEmpty_AddsOne()
    {
        var machine = Bare();
        machine.Designers = ["Steve Ritchie"];
        Assert.Equal(1, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_EditionsNonEmpty_AddsOne()
    {
        var machine = Bare();
        machine.Editions = [new MachineEdition { Name = "Pro" }];
        Assert.Equal(1, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_OpdbSourceUrlSet_AddsOne()
    {
        var machine = Bare();
        machine.OpdbSourceUrl = "https://opdb.org/machines/TEST-001";
        Assert.Equal(1, MachineCompleteness.Score(machine));
    }

    [Fact]
    public void Score_ManufacturerDisplayNameNonEmpty_AddsOne()
    {
        // ManufacturerDisplayName is required init-only, so build a fresh
        // machine with the field set rather than mutating Bare().
        var machine = new Machine
        {
            Id = "TEST-001",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Test Machine",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        Assert.Equal(1, MachineCompleteness.Score(machine));
    }

    // ── Boundary: all fields present ─────────────────────────────────────

    [Fact]
    public void Score_AllFields_ReturnsSix()
    {
        // Maximum score is 6: every scored field is populated.
        Assert.Equal(6, MachineCompleteness.Score(Rich()));
    }

    // ── Ordering invariant: richer record always scores higher ────────────

    [Fact]
    public void Score_RichBeatsEmpty()
    {
        // A fully-populated machine must score higher than an empty one.
        // This is the core invariant that ADR-0049 relies on for tie-breaking.
        Assert.True(MachineCompleteness.Score(Rich()) > MachineCompleteness.Score(Bare()));
    }

    [Fact]
    public void Score_PartiallyPopulated_BetweenZeroAndSix()
    {
        // A machine with Year + ManufacturerDisplayName = 2 points.
        // ManufacturerDisplayName is required init-only, so build a fresh machine.
        var machine = new Machine
        {
            Id = "TEST-001",
            PartitionKey = "sega",
            ManufacturerDisplayName = "Sega",
            Title = "Test Machine",
            Year = 1998,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var score = MachineCompleteness.Score(machine);
        Assert.InRange(score, 1, 5);
        Assert.Equal(2, score);
    }

    // ── Null guard ────────────────────────────────────────────────────────

    [Fact]
    public void Score_NullMachine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MachineCompleteness.Score(null!));
    }

    // ── Determinism: same input always produces same score ────────────────

    [Fact]
    public void Score_CalledTwiceOnSameInstance_ReturnsSameValue()
    {
        var machine = Rich();
        var first = MachineCompleteness.Score(machine);
        var second = MachineCompleteness.Score(machine);
        Assert.Equal(first, second);
    }
}
