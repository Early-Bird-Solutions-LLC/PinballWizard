using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Domain;

/// <summary>
/// Unit tests for the <see cref="MachineTitleLookup"/> entity:
/// normalization rules and the parallel-array invariant maintained by
/// <see cref="MachineTitleLookup.UpsertEntry"/> /
/// <see cref="MachineTitleLookup.RemoveEntry"/>. Per ADR-0025 § 4 the
/// normalization is the contract that lets dual-writes from
/// <c>OpdbSyncService</c> agree with point-reads from
/// <c>MachineGroundingTool</c> on which row to address.
/// </summary>
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

        lookup.UpsertEntry("GRBN-MQR4P", "stern");

        Assert.Equal(["GRBN-MQR4P"], lookup.OpdbIds);
        Assert.Equal(["stern"], lookup.Manufacturers);
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
        lookup.UpsertEntry("GRBN-AAA", "stern");
        lookup.UpsertEntry("GRBN-BBB", "jjp");

        // Re-upsert GRBN-AAA — should move to the end.
        lookup.UpsertEntry("GRBN-AAA", "stern");

        Assert.Equal(["GRBN-BBB", "GRBN-AAA"], lookup.OpdbIds);
        Assert.Equal(["jjp", "stern"], lookup.Manufacturers);
    }

    [Fact]
    public void RemoveEntry_ReturnsTrueAndRemovesPair()
    {
        var lookup = NewLookup("godzilla");
        lookup.UpsertEntry("GRBN-AAA", "stern");
        lookup.UpsertEntry("GRBN-BBB", "jjp");

        var removed = lookup.RemoveEntry("GRBN-AAA");

        Assert.True(removed);
        Assert.Equal(["GRBN-BBB"], lookup.OpdbIds);
        Assert.Equal(["jjp"], lookup.Manufacturers);
    }

    [Fact]
    public void RemoveEntry_MissingId_ReturnsFalseAndPreservesArrays()
    {
        var lookup = NewLookup("foo fighters");
        lookup.UpsertEntry("GRBN-MQR4P", "stern");

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
            lookup.UpsertEntry($"ID-{i:D3}", $"mfr-{i}");
        }
        Assert.Equal(5, lookup.OpdbIds.Count);
        Assert.Equal(5, lookup.Manufacturers.Count);
    }

    private static MachineTitleLookup NewLookup(string normalized) => new()
    {
        Id = normalized,
        PartitionKey = normalized,
    };
}
