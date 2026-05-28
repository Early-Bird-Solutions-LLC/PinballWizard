using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Domain;

public sealed class MachineTitleLookupTests
{
    [Theory]
    [InlineData("Foo Fighters", "foo fighters")]
    [InlineData("FOO FIGHTERS", "foo fighters")]
    [InlineData("  Foo Fighters  ", "foo fighters")]
    [InlineData("Stranger Things", "stranger things")]
    public void NormalizeTitle_LowercaseAndTrim(string input, string expected)
    {
        Assert.Equal(expected, MachineTitleLookup.NormalizeTitle(input));
    }

    [Theory]
    [InlineData("AC/DC", "ac_dc")]
    [InlineData("Star Trek?", "star trek_")]
    [InlineData("Hash#Title", "hash_title")]
    [InlineData("Slash\\Back", "slash_back")]
    public void NormalizeTitle_EscapesCosmosForbiddenChars(string input, string expected)
    {
        // Cosmos document ids cannot contain '/', '\\', '?', or '#'.
        // The normalize helper substitutes '_' so the value is safe to
        // use as both the document id and the partition-key value.
        Assert.Equal(expected, MachineTitleLookup.NormalizeTitle(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NormalizeTitle_RejectsBlank(string? input)
    {
        Assert.ThrowsAny<ArgumentException>(() => MachineTitleLookup.NormalizeTitle(input!));
    }

    [Fact]
    public void UpsertEntry_AddsNewPair()
    {
        var lookup = NewLookup("foo fighters");

        lookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]);

        Assert.Equal(["GRBN-MQR4P"], lookup.OpdbIds);
        Assert.Equal(["stern"], lookup.Manufacturers);
        Assert.Equal([["stern"]], lookup.MatchTokens);
    }

    [Fact]
    public void UpsertEntry_DuplicateOpdbId_ReplacesAndAppends()
    {
        // First-seen ordering is the contract MachineGroundingTool
        // depends on (the tool returns the first entry on the row).
        // Re-upserting an existing OPDB id keeps that contract by
        // moving it to the END of the list — a fresh insert from a
        // re-run of OPDB sync should not promote a stale ordering.
        var lookup = NewLookup("godzilla");
        lookup.UpsertEntry("GRBN-AAA", "stern", ["stern"]);
        lookup.UpsertEntry("GRBN-BBB", "jjp", ["jjp"]);

        // Re-upsert GRBN-AAA — should move to the end.
        lookup.UpsertEntry("GRBN-AAA", "stern", ["stern"]);

        Assert.Equal(["GRBN-BBB", "GRBN-AAA"], lookup.OpdbIds);
        Assert.Equal(["jjp", "stern"], lookup.Manufacturers);
    }

    [Fact]
    public void RemoveEntry_ReturnsTrueAndRemovesPair()
    {
        var lookup = NewLookup("godzilla");
        lookup.UpsertEntry("GRBN-AAA", "stern", ["stern"]);
        lookup.UpsertEntry("GRBN-BBB", "jjp", ["jjp"]);

        var removed = lookup.RemoveEntry("GRBN-AAA");

        Assert.True(removed);
        Assert.Equal(["GRBN-BBB"], lookup.OpdbIds);
        Assert.Equal(["jjp"], lookup.Manufacturers);
        Assert.Equal([["jjp"]], lookup.MatchTokens);
    }

    [Fact]
    public void RemoveEntry_MissingId_ReturnsFalseAndPreservesArrays()
    {
        var lookup = NewLookup("foo fighters");
        lookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]);

        var removed = lookup.RemoveEntry("GRBN-DOES-NOT-EXIST");

        Assert.False(removed);
        Assert.Equal(["GRBN-MQR4P"], lookup.OpdbIds);
        Assert.Equal(["stern"], lookup.Manufacturers);
    }

    [Fact]
    public void UpsertEntry_PreservesParallelArrayLengths()
    {
        // Defensive invariant: the OpdbIds and Manufacturers lists
        // MUST stay equal-length. Failure here would mean the lookup
        // doc could ship with mismatched arrays — silent corruption.
        var lookup = NewLookup("multi");
        for (int i = 0; i < 5; i++)
        {
            lookup.UpsertEntry($"ID-{i:D3}", $"mfr-{i}", [$"mfr-{i}"]);
        }
        Assert.Equal(5, lookup.OpdbIds.Count);
        Assert.Equal(5, lookup.Manufacturers.Count);
        Assert.Equal(5, lookup.MatchTokens!.Count);
    }

    [Fact]
    public void UpsertEntry_NewEntry_StoresMatchTokensAtSameIndex()
    {
        var lookup = new MachineTitleLookup { Id = "godzilla", PartitionKey = "godzilla" };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);

        Assert.Equal(2, lookup.OpdbIds.Count);
        Assert.Equal(2, lookup.MatchTokens!.Count);
        Assert.Equal(["stern"], lookup.MatchTokens[0]);
        Assert.Equal(["sega"],  lookup.MatchTokens[1]);
    }

    [Fact]
    public void UpsertEntry_ReplaceExisting_UpdatesMatchTokensInPlace()
    {
        var lookup = new MachineTitleLookup { Id = "godzilla", PartitionKey = "godzilla" };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        // Re-upsert same opdbId with new tokens (simulates sync updating a row)
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern", "newtoken"]);

        Assert.Single(lookup.OpdbIds);
        Assert.Single(lookup.MatchTokens!);
        Assert.Equal(["stern", "newtoken"], lookup.MatchTokens![0]);
    }

    [Fact]
    public void RemoveEntry_ExistingEntry_RemovesMatchTokensAtSameIndex()
    {
        var lookup = new MachineTitleLookup { Id = "godzilla", PartitionKey = "godzilla" };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);

        var removed = lookup.RemoveEntry("GweeP-MW95j");

        Assert.True(removed);
        Assert.Single(lookup.OpdbIds);
        Assert.Single(lookup.MatchTokens!);
        Assert.Equal("G5po2-MeP6B", lookup.OpdbIds[0]);
        Assert.Equal(["sega"], lookup.MatchTokens![0]);
    }

    [Fact]
    public void UpsertEntry_MultiTokenManufacturer_StoresAllTokens()
    {
        var lookup = new MachineTitleLookup { Id = "pirates of the caribbean", PartitionKey = "pirates of the caribbean" };
        lookup.UpsertEntry("GR7ZX-MQ23b", "stern", ["stern"]);
        lookup.UpsertEntry("GRbPY-MePOP", "jjp",   ["jjp", "jersey", "jack"]);

        Assert.Equal(["jjp", "jersey", "jack"], lookup.MatchTokens![1]);
    }

    [Fact]
    public void RemoveEntry_LegacyNullMatchTokens_PadsBeforeRemoval()
    {
        // Simulates a Cosmos row written before MatchTokens existed (MatchTokens == null).
        // RemoveEntry must pad with separate empty lists so a subsequent UpsertEntry keeps
        // all three arrays in sync. Also verifies pad entries are distinct instances so
        // mutating one does not affect others.
        var lookup = new MachineTitleLookup
        {
            Id = "godzilla",
            PartitionKey = "godzilla",
            OpdbIds = ["G5po2-MeP6B", "GweeP-MW95j"],
            Manufacturers = ["sega", "stern"],
            MatchTokens = null,
        };

        var removed = lookup.RemoveEntry("G5po2-MeP6B");

        Assert.True(removed);
        Assert.Single(lookup.OpdbIds);
        Assert.Equal("GweeP-MW95j", lookup.OpdbIds[0]);
        Assert.NotNull(lookup.MatchTokens);
        Assert.Single(lookup.MatchTokens);
        // Pad entry must be an empty list, not null
        Assert.Empty(lookup.MatchTokens[0]);
    }

    [Fact]
    public void RemoveEntry_LegacyNullMatchTokens_PadEntriesAreDistinctInstances()
    {
        // Verify Enumerable.Range (not Repeat) so mutating one pad does not alias others.
        // Use a 3-entry lookup so 2 pad entries remain after removal.
        var lookup = new MachineTitleLookup
        {
            Id = "test3",
            PartitionKey = "test3",
            OpdbIds = ["A", "B", "C"],
            Manufacturers = ["stern", "sega", "jjp"],
            MatchTokens = null,
        };

        lookup.RemoveEntry("A"); // pads 3 entries, removes index 0

        Assert.Equal(2, lookup.MatchTokens!.Count);
        // Must be distinct instances
        Assert.NotSame(lookup.MatchTokens[0], lookup.MatchTokens[1]);
    }

    [Fact]
    public void UpsertEntry_LegacyNullMatchTokens_PadsBeforeRemoveOnCollisionRow()
    {
        // Regression: a Cosmos collision row written before MatchTokens existed has
        // MatchTokens=null but OpdbIds/Manufacturers with 2+ entries. UpsertEntry must
        // pad MatchTokens to OpdbIds.Count *before* calling RemoveAt(idx), otherwise
        // RemoveAt throws ArgumentOutOfRangeException on the freshly-initialised empty list.
        var lookup = new MachineTitleLookup
        {
            Id = "godzilla",
            PartitionKey = "godzilla",
            OpdbIds = ["G5po2-MeP6B", "GweeP-MW95j"],
            Manufacturers = ["sega", "stern"],
            MatchTokens = null,
        };

        // Re-upserting an existing id is what the OPDB sync does on every run.
        // This must not throw.
        lookup.UpsertEntry("G5po2-MeP6B", "sega", ["sega"]);

        Assert.Equal(2, lookup.OpdbIds.Count);
        Assert.Equal(2, lookup.MatchTokens!.Count);
        Assert.Equal(["sega"], lookup.MatchTokens[1]); // moved to end by upsert
    }

    private static MachineTitleLookup NewLookup(string normalized) => new()
    {
        Id = normalized,
        PartitionKey = normalized,
    };
}
