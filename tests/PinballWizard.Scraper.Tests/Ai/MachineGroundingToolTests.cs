using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

public sealed class MachineGroundingToolTests
{
    [Fact]
    public async Task GetMachineByTitleAsync_ReturnsFirstMatch_MappedToDto()
    {
        var machine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Foo Fighters",
            Year = 2023,
            Themes = ["rock", "music"],
            Designers = ["George Gomez"],
            OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P",
            Editions =
            [
                new MachineEdition { Name = "Pro", Msrp = "$7,000" },
                new MachineEdition { Name = "Premium", Msrp = "$9,500" },
            ],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machine));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        Assert.Equal("Foo Fighters", result.Title);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        Assert.Equal(2023, result.Year);
        Assert.Equal(["rock", "music"], result.Themes);
        Assert.Equal(["George Gomez"], result.Designers);
        Assert.Equal("https://opdb.org/machines/GRBN-MQR4P", result.OpdbSourceUrl);
        Assert.Equal(2, result.Editions.Count);
        Assert.Equal("Pro", result.Editions[0].Name);
        Assert.Equal("$7,000", result.Editions[0].Msrp);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoMatch_ReturnsNull()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("nonexistent", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_EmptyTitle_ReturnsNullWithoutQuerying()
    {
        var repo = Substitute.For<IMachineRepository>();

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync(string.Empty, CancellationToken.None);

        Assert.Null(result);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_WhitespaceTitle_ReturnsNullWithoutQuerying()
    {
        var repo = Substitute.For<IMachineRepository>();

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("   ", CancellationToken.None);

        Assert.Null(result);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_MultipleMatches_ReturnsFirst()
    {
        var first = NewMachine("GRBN-MQR4P", "Foo Fighters", 2023);
        var second = NewMachine("GRBN-XYZZZ", "Foo Fighters", 1992); // hypothetical re-issue

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(first, second));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
    }

    // ── ADR-0025 § 4 point-read path ─────────────────────────────────────
    // PR 5 of the Cosmos for User Delight track replaces the cross-
    // partition `STRINGEQUALS` query with a two-point-read path: first
    // the `machine_title_lookups` materialized view, then `machines`
    // by (opdb_id, manufacturer). The cross-partition query survives as
    // a logged-warning fallback for the unmigrated-lookup case.

    [Fact]
    public async Task GetMachineByTitleAsync_LookupHit_UsesPointReadAndDoesNotQueryFallback()
    {
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Foo Fighters"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Foo Fighters"),
        };
        lookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-MQR4P", "Foo Fighters", 2023));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        // Point-read path bypasses QueryByTitleAsync entirely.
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LookupHitButMachineMissing_FallsBackAndStillResolves()
    {
        // Stale lookup — the lookup row points at a machine that no
        // longer exists in the `machines` container. The tool must
        // fall through to the cross-partition fallback so the user
        // query still resolves; the next OPDB sync will fix the row.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Stranger Things"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Stranger Things"),
        };
        lookup.UpsertEntry("GRBN-STALE", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Stranger Things", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        // The lookup-pointed-at machine is gone (point-read returns null).
        repo.GetByOpdbIdAsync("GRBN-STALE", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        // The fallback cross-partition query still finds a (different) match.
        var current = NewMachine("GRBN-CURRENT", "Stranger Things", 2024);
        repo.QueryByTitleAsync("Stranger Things", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(current));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stranger Things", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-CURRENT", result!.OpdbId);
        repo.Received(1).QueryByTitleAsync("Stranger Things", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_ScoredEntryMissing_FallsBackToCrossPartitionQuery()
    {
        // Scoring elevates a non-index-0 entry (Stern wins "Stern Godzilla"),
        // but that entry's machine row is absent (stale lookup). The tool must
        // fall through to the cross-partition fallback and still resolve.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
        };
        lookup.UpsertEntry("G5po2-MeP6B", "sega", ["sega"]);   // index 0 — not picked
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);   // index 1 — scored best, but stale

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Stern Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        // Scored entry (Stern) is missing from the machines container.
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        // Fallback cross-partition query finds a current record.
        var current = NewMachine("GweeP-MW95j-current", "Godzilla", 2021);
        repo.QueryByTitleAsync("Stern Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(current));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j-current", result!.OpdbId);
        repo.Received(1).QueryByTitleAsync("Stern Godzilla", Arg.Any<CancellationToken>());
        // Sega entry must never be fetched — scoring correctly chose Stern first.
        await repo.DidNotReceive().GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LookupMissing_FallsBackToCrossPartitionQuery()
    {
        // Lookup row doesn't exist (post-deploy backfill pending, or a
        // transient lookup-write failure during OPDB sync). The tool
        // must still resolve the answer via the fallback path.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns((MachineTitleLookup?)null);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(NewMachine("GRBN-GODZ", "Godzilla", 2021)));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-GODZ", result!.OpdbId);
        repo.Received(1).QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LookupHitWithCollision_ReturnsFirstEntry()
    {
        // Title collisions (e.g. multiple "Godzilla" releases) are
        // stored as parallel entries on the same row. The tool returns
        // the first entry's machine, matching the existing first-hit
        // semantics from the cross-partition query path.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Godzilla"),
        };
        lookup.UpsertEntry("GRBN-G1995", "sega", ["sega"]);
        lookup.UpsertEntry("GRBN-G2021", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-G1995", "sega", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-G1995", "Godzilla", 1995));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-G1995", result!.OpdbId);
        // Only the first entry's GetByOpdbIdAsync should fire — no
        // second-entry follow-up unless the first one was missing.
        await repo.DidNotReceive().GetByOpdbIdAsync("GRBN-G2021", "stern", Arg.Any<CancellationToken>());
    }

    // ── ADR-0029 S5: sibling-returning behavior ───────────────────────
    // After resolving the primary machine, if it has a GroupId the tool
    // fetches all base-machine records in the same group so the agent
    // can ask a targeted clarifying question for version-dependent
    // questions. The primary machine is excluded from Siblings.

    [Fact]
    public async Task GetMachineByTitleAsync_WithGroupId_FetchesSiblingsAndExcludesPrimary()
    {
        // Fixture: Godzilla Pro (GweeP-MW95j) resolved as primary.
        // Group GweeP also contains Premium/LE (GweeP-Ml9pZ).
        // Sibling fetch returns both; only the non-primary should land in Siblings.
        var primary = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Pro",
            Year = 2021,
            GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Pro", Msrp = "$7,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sibling = new Machine
        {
            Id = "GweeP-Ml9pZ",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Premium/LE",
            Year = 2021,
            GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Premium", Msrp = "$9,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));
        // GetSiblingsByGroupIdAsync returns both primary and sibling —
        // the tool must exclude the primary from Siblings.
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary, sibling));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Equal("GweeP", result.GroupId);

        // Siblings contains the Premium/LE but NOT the primary Pro.
        Assert.Single(result.Siblings);
        Assert.Equal("GweeP-Ml9pZ", result.Siblings[0].OpdbId);
        Assert.Equal("Godzilla Premium/LE", result.Siblings[0].Title);
        Assert.Equal("Premium", result.Siblings[0].Editions[0].Name);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoGroupId_ReturnEmptySiblings()
    {
        // A machine with no GroupId (solo title, no OPDB group) must
        // return an empty Siblings list — not null — so the agent prompt
        // can safely check Siblings.Count without null-checking.
        var machine = NewMachine("GRBN-SOLO", "Solo Title", 2020);
        // GroupId is null by default in NewMachine.

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Solo Title", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machine));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Solo Title", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.GroupId);
        Assert.Empty(result.Siblings);
        // GetSiblingsByGroupIdAsync must NOT be called when GroupId is absent.
        repo.DidNotReceiveWithAnyArgs().GetSiblingsByGroupIdAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_SiblingFetchFails_StillReturnsPrimary()
    {
        // Sibling fetch is best-effort. A Cosmos failure during sibling
        // fetch must NOT prevent the primary machine from being returned.
        // The tool degrades to single-machine mode (empty Siblings).
        var primary = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Pro",
            Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla Pro", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Cosmos unavailable"));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);

        // Must not throw — degrades gracefully.
        var result = await tool.GetMachineByTitleAsync("Godzilla Pro", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Empty(result.Siblings);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_GroupWithOnlyPrimary_ReturnEmptySiblings()
    {
        // Group fetch returns only the primary machine (sole member of
        // its group in the catalog — not yet backfilled with siblings).
        // Siblings must be empty, not contain the primary itself.
        var primary = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Pro",
            Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla Pro", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));
        // Only the primary in the group (sibling fetch returns only it).
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla Pro", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.Siblings);
    }

    // ── Task 2: manufacturer/year scoring ──────────────────────────────────

    [Fact]
    public void ScoreEntryAgainstTokens_ManufacturerMatch_ReturnsOne()
    {
        // "Stern Godzilla" tokens: ["stern", "godzilla"]
        // Entry manufacturer "stern" → exactly 1 token match
        var tokens = MachineGroundingTool.TokenizeForOverlap("Stern Godzilla");
        var score = MachineGroundingTool.ScoreEntryAgainstTokens(["stern"], tokens);
        Assert.Equal(1, score);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_ManufacturerNoMatch_ReturnsZero()
    {
        var tokens = MachineGroundingTool.TokenizeForOverlap("Stern Godzilla");
        var score = MachineGroundingTool.ScoreEntryAgainstTokens(["sega"], tokens);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_ManufacturerInTitle_ScoresHigherThanMismatch()
    {
        // Confirms the scoring correctly distinguishes manufacturer presence vs absence.
        // MachineTitleLookup has no year column; scoring is manufacturer-token-only.
        var tokens = MachineGroundingTool.TokenizeForOverlap("Stern Godzilla");
        var sternScore = MachineGroundingTool.ScoreEntryAgainstTokens(["stern"], tokens);
        var segaScore = MachineGroundingTool.ScoreEntryAgainstTokens(["sega"], tokens);
        Assert.True(sternScore > segaScore);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_NoManufacturerSignal_ReturnsZero()
    {
        // bare "Godzilla" has no manufacturer qualifier — all entries score 0
        // and insertion order wins (backward-compatible behaviour)
        var tokens = MachineGroundingTool.TokenizeForOverlap("Godzilla");
        Assert.Equal(0, MachineGroundingTool.ScoreEntryAgainstTokens(["sega"], tokens));
        Assert.Equal(0, MachineGroundingTool.ScoreEntryAgainstTokens(["stern"], tokens));
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionWithManufacturerQualifier_ResolvesCorrectEntry()
    {
        // "Stern Godzilla" should resolve to Stern 2021 (GweeP-Ml9pZ), NOT
        // Sega 1998 (G5po2-MeP6B), even though Sega is at index 0 of the
        // lookup row. The manufacturer token "stern" in the title breaks the tie.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
        };
        // Sega first (index 0), Stern second — insertion-order would pick Sega.
        lookup.UpsertEntry("G5po2-MeP6B", "sega", ["sega"]);
        lookup.UpsertEntry("GweeP-Ml9pZ", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Stern Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = "GweeP-Ml9pZ", PartitionKey = "stern",
                ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
                FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            });

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-Ml9pZ", result!.OpdbId);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        // Sega's GetByOpdbIdAsync must never be called.
        await repo.DidNotReceive().GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionNoQualifier_ReturnsBestInsertionOrder()
    {
        // Bare "Godzilla" — no manufacturer/year tokens — all scores = 0.
        // Falls back to insertion order: first entry (Sega) wins.
        // This preserves existing behaviour for unqualified lookups.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Godzilla"),
        };
        lookup.UpsertEntry("G5po2-MeP6B", "sega", ["sega"]);
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = "G5po2-MeP6B", PartitionKey = "sega",
                ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
                FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            });

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("G5po2-MeP6B", result!.OpdbId);
    }

    // ── Task 4: full Sega/Stern scenario ─────────────────────────────────

    [Fact]
    public async Task GetMachineByTitleAsync_SternGodzillaScenario_NeverReturnsSega()
    {
        // Full realistic scenario matching the H5 eval failure:
        // User says "Stern Godzilla". Lookup row has Sega 1998 at [0],
        // Stern 2021 at [1]. Scoring must pick Stern.
        // Siblings (Premium/LE within GweeP) are also returned.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
        };
        lookup.UpsertEntry("G5po2-MeP6B", "sega", ["sega"]);   // index 0 — old loser
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);   // index 1 — correct

        var sternPro = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla",
            Year = 2021, GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Pro", Msrp = "$7,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sternPremiumLe = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla",
            Year = 2021, GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Premium", Msrp = "$9,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Stern Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns(sternPro);
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(sternPro, sternPremiumLe));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        Assert.Equal(2021, result.Year);
        // Sega must never be fetched.
        await repo.DidNotReceive().GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>());
        // Siblings (Premium/LE) are surfaced for the clarifying question path.
        Assert.Single(result.Siblings);
        Assert.Equal("GweeP-Ml9pZ", result.Siblings[0].OpdbId);
    }

    [Fact]
    public void Ctor_NullRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(null!, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance));
    }

    [Fact]
    public void Ctor_NullTitleLookups_Throws()
    {
        var repo = Substitute.For<IMachineRepository>();
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(repo, null!, NullLogger<MachineGroundingTool>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var repo = Substitute.For<IMachineRepository>();
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), null!));
    }

    // ── pinwiz.ai.tool_duration_ms emission ──────────────────────────────
    // Stopwatch + try/finally produce exactly one tool_duration_ms sample
    // per invocation, tagged tool=getMachineByTitle. The whitespace-input
    // short-circuit fires before the Stopwatch starts so no-op prompts
    // don't poison the latency distribution.

    [Fact]
    public async Task GetMachineByTitleAsync_Match_EmitsToolDurationMs_TaggedGetMachineByTitle()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(NewMachine("GRBN-MQR4P", "Foo Fighters", 2023)));

        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
            await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);
        }

        AssertOurToolEmittedAtLeastOnce(samples);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoMatch_StillEmitsToolDurationMs()
    {
        // The "no match" path returns null but spends real wall-clock on
        // the repository query. Latency is still operationally meaningful
        // — slow misses are a different signal from fast misses — so the
        // emission must fire here too.
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
            var result = await tool.GetMachineByTitleAsync("nonexistent", CancellationToken.None);
            Assert.Null(result);
        }

        AssertOurToolEmittedAtLeastOnce(samples);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_WhitespaceTitle_DoesNotEmitToolDurationMs()
    {
        // Whitespace short-circuit fires before Stopwatch.StartNew, so
        // dashboards don't see noise samples for inputs the orchestrator
        // accidentally produced. Pinned so a future refactor doesn't add
        // a no-op-path sample inadvertently.
        var repo = Substitute.For<IMachineRepository>();
        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
            await tool.GetMachineByTitleAsync("   ", CancellationToken.None);
        }

        Assert.DoesNotContain(samples, s => s.ToolTag == MachineGroundingTool.ToolTagValue);
    }

    // The instrument is a single process-global Meter, so emissions from
    // SearchCorpusToolTests running in parallel with this class will land
    // in our listener too. We assert only on emissions tagged with this
    // tool's name — that's what the test actually cares about. We also
    // do NOT assert sample count == 1, because in parallel test runs
    // the same tool may emit again from concurrent test executions; the
    // emission *contract* is "tool fires this metric on every call" and
    // a Contains assertion captures that without coupling to scheduler.
    private static void AssertOurToolEmittedAtLeastOnce(
        IEnumerable<(double Value, string? ToolTag)> samples)
    {
        Assert.Contains(
            samples,
            s => s.ToolTag == MachineGroundingTool.ToolTagValue && s.Value >= 0);
    }

    private static ConcurrentBag<(double Value, string? ToolTag)> CollectToolDurationSamples(out MeterListener listener)
    {
        // Force `PinballWizardTelemetry`'s static cctor to complete first
        // so the instrument exists when we wire the listener. Explicitly
        // enabling the named instrument after `Start()` is more
        // deterministic than relying on the `InstrumentPublished` delivery
        // path. ConcurrentBag handles the concurrency: parallel test
        // classes that emit to the same process-global Meter cause
        // concurrent measurement callbacks on this listener (each test
        // class emits to AiToolDurationMs from its own threads), so the
        // sample collection must be thread-safe.
        var samples = new ConcurrentBag<(double Value, string? ToolTag)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? toolTag = null;
            foreach (var t in tags)
            {
                if (t.Key == "tool")
                {
                    toolTag = t.Value as string;
                }
            }
            samples.Add((value, toolTag));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.AiToolDurationMs);
        listener = l;
        return samples;
    }

    // ── Task 4: ScoreEntryAgainstTokens (IReadOnlyList<string> signature) ───

    [Fact]
    public void ScoreEntryAgainstTokens_SingleToken_ExactMatch_ReturnsOne()
    {
        var score = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["stern"],
            titleTokens: ["stern", "godzilla"]);
        Assert.Equal(1, score);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_MultiToken_PartialMatch_ReturnsMatchCount()
    {
        var score = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["jjp", "jersey", "jack"],
            titleTokens: ["jersey", "jack", "pirates"]);
        Assert.Equal(2, score);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_NoOverlap_ReturnsZero()
    {
        var score = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["cgc", "chicago", "gaming"],
            titleTokens: ["stern", "godzilla"]);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_JjpVsStern_JerseyJackWins()
    {
        var jjpScore = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["jjp", "jersey", "jack"],
            titleTokens: ["jersey", "jack", "pirates"]);
        var sternScore = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["stern"],
            titleTokens: ["jersey", "jack", "pirates"]);
        Assert.True(jjpScore > sternScore);
    }

    [Fact]
    public void ScoreEntryAgainstTokens_CgcVsBally_ChicagoGamingWins()
    {
        var cgcScore = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["cgc", "chicago", "gaming"],
            titleTokens: ["chicago", "gaming", "attack", "mars"]);
        var ballyScore = MachineGroundingTool.ScoreEntryAgainstTokens(
            matchTokens: ["bally"],
            titleTokens: ["chicago", "gaming", "attack", "mars"]);
        Assert.True(cgcScore > ballyScore);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_JjpQualifier_PicksJjpOverStern()
    {
        var lookup = new MachineTitleLookup
        {
            Id = "pirates of the caribbean",
            PartitionKey = "pirates of the caribbean",
        };
        lookup.UpsertEntry("GR7ZX-MQ23b", "stern", ["stern"]);
        lookup.UpsertEntry("GRbPY-MePOP", "jjp",   ["jjp", "jersey", "jack"]);

        var sternMachine = BuildMachine("GR7ZX-MQ23b", "stern", "Stern Pinball", "Pirates of the Caribbean", 2006);
        var jjpMachine   = BuildMachine("GRbPY-MePOP", "jjp",   "Jersey Jack Pinball", "Pirates of the Caribbean", 2019);

        var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
        titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

        var machines = Substitute.For<IMachineRepository>();
        machines.GetByOpdbIdAsync("GRbPY-MePOP", "jjp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Machine?>(jjpMachine));
        machines.GetByOpdbIdAsync("GR7ZX-MQ23b", "stern", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Machine?>(sternMachine));
        machines.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(machines, titleLookups, NullLogger<MachineGroundingTool>.Instance);

        var result = await tool.GetMachineByTitleAsync("Jersey Jack Pirates of the Caribbean", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRbPY-MePOP", result!.OpdbId);
        await machines.DidNotReceive().GetByOpdbIdAsync("GR7ZX-MQ23b", "stern", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NullMatchTokens_FallsBackToManufacturerKeyScoring()
    {
        var lookup = new MachineTitleLookup
        {
            Id = "godzilla",
            PartitionKey = "godzilla",
            OpdbIds = ["G5po2-MeP6B", "GweeP-MW95j"],
            Manufacturers = ["sega", "stern"],
            MatchTokens = null,
        };

        var sternMachine = BuildMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla", 2021);

        var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
        titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

        var machines = Substitute.For<IMachineRepository>();
        machines.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Machine?>(sternMachine));
        machines.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(machines, titleLookups, NullLogger<MachineGroundingTool>.Instance);

        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_MismatchedMatchTokensLength_FallsBackToCrossPartition()
    {
        // MatchTokens.Count = 1 but OpdbIds.Count = 2 → corruption, must fall back
        var lookup = new MachineTitleLookup
        {
            Id = "godzilla",
            PartitionKey = "godzilla",
            OpdbIds = ["G5po2-MeP6B", "GweeP-MW95j"],
            Manufacturers = ["sega", "stern"],
            MatchTokens = [["sega"]],   // length 1, mismatched
        };

        var sternMachine = BuildMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla", 2021);

        var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
        titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

        var machines = Substitute.For<IMachineRepository>();
        machines.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new[] { sternMachine }));
        machines.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(machines, titleLookups, NullLogger<MachineGroundingTool>.Instance);

        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        // Falls back to cross-partition QueryByTitleAsync and gets a result
        machines.Received(1).QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static Machine NewMachine(string id, string title, int year) => new()
    {
        Id = id,
        PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball",
        Title = title,
        Year = year,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static Machine BuildMachine(string id, string partitionKey, string displayName, string title, int year) => new()
    {
        Id = id,
        PartitionKey = partitionKey,
        ManufacturerDisplayName = displayName,
        Title = title,
        Year = year,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}