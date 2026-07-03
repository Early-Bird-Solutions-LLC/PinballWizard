using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Catalog;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="CatalogStatsRebuildService.BuildRollups"/>.
///
/// The pure aggregation method is tested without any Container mock — it takes
/// a sequence of (Machine, docTypeCounts) pairs and a fixed AsOf timestamp, and
/// produces per-manufacturer <see cref="CatalogStatsCosmosRecord"/> rollups with
/// AUTHORITATIVE identity fields from the Machine record.
///
/// ADR-0036 compliance: no <c>GetItemQueryIterator</c> calls here. The I/O path
/// (<c>RebuildAsync</c>) is exercised separately via integration tests once a live
/// Cosmos emulator is available; the pure aggregation is testable entirely in memory.
/// </summary>
public sealed class CatalogStatsRebuildServiceTests
{
    private static readonly DateTimeOffset FixedAsOf =
        new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // BuildRollups — two stern machines + one jjp machine yields two rollup docs.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_TwoManufacturers_ReturnsTwoRollupRecords()
    {
        var sternA = MakeMachine("GRBN-A", "stern", "Godzilla Pro", editionLabel: "Pro",
            groupId: "GRBN", year: 2021, manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });
        var sternB = MakeMachine("GRBN-B", "stern", "Godzilla Premium", editionLabel: "Premium",
            groupId: "GRBN", year: 2021, manufacturerSlugs: new() { ["stern"] = "godzilla-premium" });
        var jjpA  = MakeMachine("WLYC-A", "jjp", "Wonky Wizard", editionLabel: null,
            groupId: "WLYC", year: 2022, manufacturerSlugs: new() { ["jjp"] = "wonky-wizard" });

        var pairs = new[]
        {
            (sternA, (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                { ["Manual"] = 2, ["Bulletin"] = 1 }),
            (sternB, (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
            (jjpA,  (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                { ["Manual"] = 1 }),
        };

        var rollups = CatalogStatsRebuildService.BuildRollups(pairs, FixedAsOf);

        Assert.Equal(2, rollups.Count);

        var stern = rollups.Single(r => r.Id == "stern");
        var jjp   = rollups.Single(r => r.Id == "jjp");

        Assert.Equal(2, stern.Machines.Count);
        Assert.Single(jjp.Machines);
    }

    // -------------------------------------------------------------------------
    // BuildRollups — stern machine with Manual → HasManual=true, DocCount correct.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_SternMachineWithManual_HasManualTrueAndDocCountCorrect()
    {
        var machine = MakeMachine("GRBN-A", "stern", "Godzilla Pro", editionLabel: "Pro",
            groupId: "GRBN", year: 2021, manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });

        var typeCounts = (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Manual"]   = 2,
            ["Bulletin"] = 1,
        };

        var rollups = CatalogStatsRebuildService.BuildRollups([(machine, typeCounts)], FixedAsOf);

        var entry = rollups.Single().Machines.Single();
        Assert.True(entry.HasManual);
        Assert.Equal(3, entry.DocCount);
        Assert.Equal(2, entry.DocTypeCounts["Manual"]);
        Assert.Equal(1, entry.DocTypeCounts["Bulletin"]);
    }

    // -------------------------------------------------------------------------
    // BuildRollups — machine with no docs → DocCount=0, HasManual=false.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_MachineWithNoDocs_DocCountZeroHasManualFalse()
    {
        var machine = MakeMachine("GRBN-B", "stern", "Godzilla Premium", editionLabel: "Premium",
            groupId: "GRBN", year: 2021, manufacturerSlugs: new() { ["stern"] = "godzilla-premium" });

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(machine, new Dictionary<string, int>())],
            FixedAsOf);

        var entry = rollups.Single().Machines.Single();
        Assert.Equal(0, entry.DocCount);
        Assert.False(entry.HasManual);
        Assert.Empty(entry.DocTypeCounts);
    }

    // -------------------------------------------------------------------------
    // BuildRollups — authoritative identity fields come from Machine record.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_IdentityFieldsAreAuthoritative_FromMachineRecord()
    {
        var machine = MakeMachine("GRBN-A", "stern", "Godzilla Pro",
            editionLabel: "Pro", groupId: "GRBN", year: 2021,
            manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(machine, new Dictionary<string, int>())],
            FixedAsOf);

        var entry = rollups.Single().Machines.Single();
        Assert.Equal("GRBN-A",     entry.MachineId);
        Assert.Equal("Godzilla Pro", entry.Title);
        Assert.Equal("Pro",        entry.EditionLabel);
        Assert.Equal("GRBN",       entry.GroupId);
        Assert.Equal(2021,         entry.Year);
    }

    // -------------------------------------------------------------------------
    // BuildRollups — rollup carries the manufacturer DISPLAY name (distinct from
    // the partition key) so consumers can render/link without a machine read.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_CarriesManufacturerDisplayName_DistinctFromKey()
    {
        var machine = new Machine
        {
            Id                      = "GRBN-A",
            PartitionKey            = "stern",           // key
            ManufacturerDisplayName = "Stern Pinball",   // display name (distinct from key)
            Title                   = "Godzilla Pro",
            ManufacturerSlugs       = new(StringComparer.OrdinalIgnoreCase) { ["stern"] = "godzilla-pro" },
            FirstSeenAt             = DateTimeOffset.UnixEpoch,
            LastSeenAt              = DateTimeOffset.UnixEpoch,
        };

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(machine, new Dictionary<string, int>())],
            FixedAsOf);

        var record = rollups.Single();
        Assert.Equal("stern", record.PartitionKey);                     // key unchanged
        Assert.Equal("Stern Pinball", record.ManufacturerDisplayName);  // display name carried
    }

    // -------------------------------------------------------------------------
    // BuildRollups — IsOpdbOnly is true when ManufacturerSlugs is empty.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_EmptyManufacturerSlugs_IsOpdbOnlyTrue()
    {
        // Machine from OPDB only — no manufacturer scraper has claimed it.
        var opdbOnlyMachine = MakeMachine("RARE-X", "stern", "Rare Old Machine",
            editionLabel: null, groupId: "RARE", year: 1998,
            manufacturerSlugs: new());   // empty — OPDB only

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(opdbOnlyMachine, new Dictionary<string, int>())],
            FixedAsOf);

        var entry = rollups.Single().Machines.Single();
        Assert.True(entry.IsOpdbOnly,
            "A machine with no manufacturer slugs should be flagged as OPDB-only.");
    }

    // -------------------------------------------------------------------------
    // BuildRollups — IsOpdbOnly is false when ManufacturerSlugs has at least one entry.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_PopulatedManufacturerSlugs_IsOpdbOnlyFalse()
    {
        var scrapedMachine = MakeMachine("GRBN-A", "stern", "Godzilla Pro",
            editionLabel: "Pro", groupId: "GRBN", year: 2021,
            manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(scrapedMachine, new Dictionary<string, int>())],
            FixedAsOf);

        var entry = rollups.Single().Machines.Single();
        Assert.False(entry.IsOpdbOnly,
            "A machine with at least one manufacturer slug should not be flagged as OPDB-only.");
    }

    // -------------------------------------------------------------------------
    // BuildRollups — AsOfUtc is stamped from the supplied argument (never UtcNow).
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_AsOfUtcStampedFromArgument()
    {
        var machine = MakeMachine("GRBN-A", "stern", "Godzilla Pro",
            editionLabel: null, groupId: "GRBN", year: 2021,
            manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });

        var fixedTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(machine, new Dictionary<string, int>())],
            fixedTime);

        Assert.All(rollups, r => Assert.Equal(fixedTime, r.AsOfUtc));
    }

    // -------------------------------------------------------------------------
    // BuildRollups — HasManual is case-insensitive ("manual" matches "Manual").
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_HasManual_CaseInsensitiveKeyLookup()
    {
        var machine = MakeMachine("GRBN-A", "stern", "Godzilla Pro",
            editionLabel: null, groupId: null, year: null,
            manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });

        // Use lowercase key — the rebuild service should handle it case-insensitively.
        var typeCounts = (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["manual"] = 1,
        };

        var rollups = CatalogStatsRebuildService.BuildRollups([(machine, typeCounts)], FixedAsOf);

        var entry = rollups.Single().Machines.Single();
        Assert.True(entry.HasManual,
            "HasManual should be true when the type-counts dictionary contains 'manual' in any case.");
    }

    // -------------------------------------------------------------------------
    // BuildRollups — rollup record id and partition key equal the manufacturer key.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_RollupRecord_IdAndPartitionKeyEqualManufacturer()
    {
        var machine = MakeMachine("WLYC-A", "jjp", "Wonky Wizard",
            editionLabel: null, groupId: "WLYC", year: 2022,
            manufacturerSlugs: new() { ["jjp"] = "wonky-wizard" });

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(machine, new Dictionary<string, int>())],
            FixedAsOf);

        var record = rollups.Single();
        Assert.Equal("jjp", record.Id);
        Assert.Equal("jjp", record.PartitionKey);
    }

    // -------------------------------------------------------------------------
    // BuildRollups — two stern machines yield a single rollup with two entries.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_TwoSternMachines_SingleRollupWithTwoEntries()
    {
        var sternA = MakeMachine("GRBN-A", "stern", "Godzilla Pro", editionLabel: "Pro",
            groupId: "GRBN", year: 2021, manufacturerSlugs: new() { ["stern"] = "godzilla-pro" });
        var sternB = MakeMachine("GRBN-B", "stern", "Godzilla Premium", editionLabel: "Premium",
            groupId: "GRBN", year: 2021, manufacturerSlugs: new() { ["stern"] = "godzilla-premium" });

        var sternADocs = (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Manual"] = 1 };
        var sternBDocs = (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var rollups = CatalogStatsRebuildService.BuildRollups(
            [(sternA, sternADocs), (sternB, sternBDocs)],
            FixedAsOf);

        var record = rollups.Single();
        Assert.Equal("stern", record.Id);
        Assert.Equal(2, record.Machines.Count);

        var proEntry     = record.Machines.Single(m => m.MachineId == "GRBN-A");
        var premiumEntry = record.Machines.Single(m => m.MachineId == "GRBN-B");

        Assert.True(proEntry.HasManual);
        Assert.Equal(1, proEntry.DocCount);
        Assert.False(premiumEntry.HasManual);
        Assert.Equal(0, premiumEntry.DocCount);
    }

    // -------------------------------------------------------------------------
    // BuildRollups — empty input yields empty list (no null reference).
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildRollups_EmptyInput_ReturnsEmptyList()
    {
        var rollups = CatalogStatsRebuildService.BuildRollups(
            [],
            FixedAsOf);

        Assert.Empty(rollups);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Machine MakeMachine(
        string id,
        string manufacturer,
        string title,
        string? editionLabel,
        string? groupId,
        int? year,
        Dictionary<string, string> manufacturerSlugs) =>
        new()
        {
            Id                    = id,
            PartitionKey          = manufacturer,
            ManufacturerDisplayName = manufacturer,
            Title                 = title,
            EditionLabel          = editionLabel,
            GroupId               = groupId,
            Year                  = year,
            ManufacturerSlugs     = new Dictionary<string, string>(manufacturerSlugs, StringComparer.OrdinalIgnoreCase),
            FirstSeenAt           = DateTimeOffset.UtcNow,
            LastSeenAt            = DateTimeOffset.UtcNow,
        };
}
