using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

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
    public async Task GetMachineByTitleAsync_LookupHitWithCollision_NewerYearWins()
    {
        // Bare "Godzilla" — no manufacturer qualifier — all entries score 0.
        // The content-intrinsic tie-break (ADR-0049) resolves by Year descending:
        // Stern 2021 beats Sega 1995 and becomes the primary result.
        // Both machines have equal completeness (Year + ManufacturerDisplayName = 2),
        // so Year is the deciding signal here.
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
        // Sega (1995) — earlier; Stern (2021) — newer. Both have equal
        // completeness, so Year is the deciding dimension.
        repo.GetByOpdbIdAsync("GRBN-G1995", "sega", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-G1995", "Godzilla", 1995));
        repo.GetByOpdbIdAsync("GRBN-G2021", "stern", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-G2021", "Godzilla", 2021));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        // Stern 2021 wins the content-intrinsic tie-break (newer Year).
        Assert.NotNull(result);
        Assert.Equal("GRBN-G2021", result!.OpdbId);
        // The losing Sega entry is surfaced via TitleCollisions so the agent
        // can disambiguate ("Sega 1995 or Stern 2021?").
        Assert.Single(result.TitleCollisions);
        Assert.Equal("GRBN-G1995", result.TitleCollisions[0].OpdbId);
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
            EditionLabel = "Premium/LE",
            EditionTokens = ["premium", "le", "70th"],
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
        // Task 7 (AB#259): EditionLabel + EditionTokens surfaced so the
        // Wizard can name the edition and match a user-named edition.
        Assert.Equal("Premium/LE", result.Siblings[0].EditionLabel);
        Assert.Equal(["premium", "le", "70th"], result.Siblings[0].EditionTokens);
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
        // Bare "Godzilla" has no manufacturer qualifier — all entries score 0.
        // When all entries are tied at zero, the content-intrinsic tie-break
        // (MachineCompleteness.Score + Year) resolves the winner instead of
        // insertion order (ADR-0049 Phase 1 decision).
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
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = "G5po2-MeP6B", PartitionKey = "sega", GroupId = "G5po2",
                ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
                FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            });

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-Ml9pZ", result!.OpdbId);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        // ADR-0029 follow-up 2026-06-10: the losing Sega entry is fetched for
        // TitleCollisions (cross-group disambiguation) — but it must never be
        // the RESULT. The qualifier still wins the resolution.
        Assert.Single(result.TitleCollisions);
        Assert.Equal("G5po2-MeP6B", result.TitleCollisions[0].OpdbId);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionNoQualifier_ContentIntrinsic_HigherCompletenessWins()
    {
        // Bare "Godzilla" — no manufacturer/year tokens — all entries score 0.
        // The content-intrinsic tie-break (ADR-0049) fetches both candidates and
        // compares MachineCompleteness.Score. CRITICAL for isolating the signal:
        // the SPARSER Stern (score=2) is at index 0 and the RICHER Sega (Themes +
        // Designers, score=4) is at index 1. The OLD insertion-order tie-break
        // would return Stern (index 0); the new completeness-first tie-break
        // returns Sega — so this fixture actually distinguishes new from old.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Godzilla"),
        };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);   // index 0 — sparser; insertion-order would pick this
        lookup.UpsertEntry("G5po2-MeP6B", "sega", ["sega"]);     // index 1 — richer; only completeness picks this

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        // Sega — older Year but richer record: completeness score = 4.
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = "G5po2-MeP6B", PartitionKey = "sega",
                ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
                Themes = ["sci-fi", "monsters"],
                Designers = ["Pat Lawlor"],
                FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            });
        // Stern — newer Year but sparser record: completeness score = 2.
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = "GweeP-MW95j", PartitionKey = "stern",
                ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
                FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            });

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        // Sega wins because its completeness score (4) outranks Stern's (2).
        Assert.NotNull(result);
        Assert.Equal("G5po2-MeP6B", result!.OpdbId);
        // Stern is surfaced via TitleCollisions.
        Assert.Single(result.TitleCollisions);
        Assert.Equal("GweeP-MW95j", result.TitleCollisions[0].OpdbId);
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

        var segaGodzilla = new Machine
        {
            Id = "G5po2-MeP6B", PartitionKey = "sega", GroupId = "G5po2",
            ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns(sternPro);
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>())
            .Returns(segaGodzilla);
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(sternPro, sternPremiumLe));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        Assert.Equal(2021, result.Year);
        // ADR-0029 follow-up 2026-06-10: Sega IS fetched — but only to be
        // surfaced in TitleCollisions (different OPDB group, same title).
        // The RESULT must still never be Sega.
        Assert.Single(result.TitleCollisions);
        Assert.Equal("G5po2-MeP6B", result.TitleCollisions[0].OpdbId);
        Assert.Equal(1998, result.TitleCollisions[0].Year);
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
        // ADR-0029 follow-up 2026-06-10: the losing Stern entry is surfaced
        // via TitleCollisions for cross-group disambiguation.
        Assert.Single(result.TitleCollisions);
        Assert.Equal("GR7ZX-MQ23b", result.TitleCollisions[0].OpdbId);
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

    // ── Prefix-strip retry (AB#259 ev-valuation-0002 fix) ────────────────
    // The LLM sends "{mfr} {title} {edition}" shapes (e.g. "Stern Godzilla
    // Premium") because the [Description] instructs it to. No lookup row
    // covers this combined shape, but "godzilla premium" (edition row) and
    // "stern godzilla" (mfr-title row) do exist. The retry must find
    // "godzilla premium" after one prefix-strip, then score against the
    // ORIGINAL tokens ("stern", "godzilla", "premium") to pick the right entry.

    [Fact]
    public async Task GetMachineByTitleAsync_ManufacturerEditionShape_ResolvesViaPrefixStripRetry()
    {
        // Live Cosmos row coverage (verified 2026-06-10):
        //   "godzilla"         → [G5po2-MeP6B/sega, GweeP-MW95j/stern, GweeP-Ml9pZ/stern]
        //   "stern godzilla"   → [GweeP-MW95j/stern, GweeP-Ml9pZ/stern]
        //   "godzilla premium" → [GweeP-Ml9pZ/stern]   ← edition row exists
        //   "stern godzilla premium" → NOT FOUND         ← first point-read misses
        //
        // After stripping "stern" the retry key is "godzilla premium" which
        // hits the edition row. Scoring with original tokens ["stern", "godzilla",
        // "premium"] resolves to GweeP-Ml9pZ (the Premium/LE machine).

        // Row for "stern godzilla premium" → missing (returns null on first call).
        // Row for "godzilla premium" → single entry for Premium/LE.
        var editionLookup = new MachineTitleLookup
        {
            Id = "godzilla premium",
            PartitionKey = "godzilla premium",
        };
        editionLookup.UpsertEntry("GweeP-Ml9pZ", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        // First call ("stern godzilla premium") misses.
        lookups.GetByTitleAsync("stern godzilla premium", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);
        // Retry call ("godzilla premium") hits.
        lookups.GetByTitleAsync("godzilla premium", Arg.Any<CancellationToken>())
            .Returns(editionLookup);

        var premiumMachine = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla Premium/LE",
            Year = 2021, GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Premium", Msrp = "$9,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>())
            .Returns(premiumMachine);
        // No siblings needed for this assertion.
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla Premium", CancellationToken.None);

        // Must resolve to the Premium/LE machine, NOT the Pro.
        Assert.NotNull(result);
        Assert.Equal("GweeP-Ml9pZ", result!.OpdbId);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        // Cross-partition fallback must never fire — retry found the row.
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_RetryHitOnCollisionRow_ScoresWithOriginalTitleTokens()
    {
        // Pins the design claim that entry scoring after a retry hit uses
        // tokens from the ORIGINAL title, not the stripped retry key. The
        // retried "godzilla premium" row here is a two-entry collision where
        // only the stripped "stern" token breaks the tie: scoring with the
        // retry key's tokens would tie at 0 and fall back to insertion order
        // (sega first — the wrong machine).
        var collisionRow = new MachineTitleLookup
        {
            Id = "godzilla premium",
            PartitionKey = "godzilla premium",
        };
        collisionRow.UpsertEntry("G5po2-MePXX", "sega", ["sega"]);
        collisionRow.UpsertEntry("GweeP-Ml9pZ", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("stern godzilla premium", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);
        lookups.GetByTitleAsync("godzilla premium", Arg.Any<CancellationToken>())
            .Returns(collisionRow);

        var premiumMachine = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla Premium/LE",
            Year = 2021, GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>())
            .Returns(premiumMachine);
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla Premium", CancellationToken.None);

        // "stern" from the original title must outscore the sega entry; if
        // scoring used the retry key's tokens this would resolve insertion-
        // order-first to G5po2-MePXX.
        Assert.NotNull(result);
        Assert.Equal("GweeP-Ml9pZ", result!.OpdbId);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_FirstReadReturnsEmptyOpdbIdsRow_StillRetries()
    {
        // A non-null lookup row with an empty OpdbIds list (partial write /
        // data corruption) is a miss for retry purposes — the prefix-strip
        // retry must still fire rather than fall straight to the
        // cross-partition fallback.
        var emptyRow = new MachineTitleLookup
        {
            Id = "stern godzilla premium",
            PartitionKey = "stern godzilla premium",
        };

        var editionLookup = new MachineTitleLookup
        {
            Id = "godzilla premium",
            PartitionKey = "godzilla premium",
        };
        editionLookup.UpsertEntry("GweeP-Ml9pZ", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("stern godzilla premium", Arg.Any<CancellationToken>())
            .Returns(emptyRow);
        lookups.GetByTitleAsync("godzilla premium", Arg.Any<CancellationToken>())
            .Returns(editionLookup);

        var premiumMachine = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla Premium/LE",
            Year = 2021, GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>())
            .Returns(premiumMachine);
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla Premium", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-Ml9pZ", result!.OpdbId);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_RetryRemainderWouldBeSingleToken_DoesNotRetry()
    {
        // "godzilla premium" has 2 tokens. Stripping 1 leaves "premium" (1 token).
        // The ≥ 2 remainder rule must prevent this retry from firing.
        // Assert via a counting fake: GetByTitleAsync must be called exactly once
        // (for the original "godzilla premium" key) and never for "premium".

        var lookupCallKeys = new List<string>();
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lookupCallKeys.Add(ci.ArgAt<string>(0));
                return Task.FromResult<MachineTitleLookup?>(null);
            });

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("godzilla premium", CancellationToken.None);

        // No match is fine — we're asserting the retry boundary, not the result.
        Assert.Null(result);
        // "premium" must never be tried; exactly 1 lookup call for the original key.
        Assert.DoesNotContain("premium", lookupCallKeys);
        Assert.Single(lookupCallKeys);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_DirectHit_NeverRetries()
    {
        // When the first point-read succeeds, no retry must fire.
        // Assert via counting: GetByTitleAsync is called exactly once.
        var lookupCallCount = 0;
        var directLookup = new MachineTitleLookup
        {
            Id = "foo fighters",
            PartitionKey = "foo fighters",
        };
        directLookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lookupCallCount++;
                return Task.FromResult<MachineTitleLookup?>(directLookup);
            });

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-MQR4P", "Foo Fighters", 2023));
        repo.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        // Exactly one lookup call — no retry.
        Assert.Equal(1, lookupCallCount);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_RetryAlsoMisses_FallsBackToCrossPartitionQuery()
    {
        // Both the original key and the retry key miss. The existing cross-partition
        // fallback must still fire and resolve the answer — unchanged behaviour.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var fallbackMachine = NewMachine("GRBN-FALLBACK", "Godzilla Premium", 2021);
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(fallbackMachine));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla Premium", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-FALLBACK", result!.OpdbId);
        // Cross-partition fallback must fire.
        repo.Received(1).QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LongQueryWhoseSuffixMatchesShortManufacturerPrefixRow_ReturnsNull()
    {
        // Regression: "Pokemon by Stern Pinball" (4 tokens) normalizes to
        // ["pokemon", "by", "stern", "pinball"]. With strip=2 the retry key
        // was "stern pinball" — which IS a real lookup row written by OPDB
        // sync phase (e) for a machine literally titled "Pinball" by Stern.
        // The tool incorrectly resolved to that machine instead of returning
        // null (no Pokemon machine exists in the catalog).
        //
        // The fix: limit prefix-strip retries to 1 so only the immediate
        // leading token is peeled off. "by stern pinball" (strip=1) is not
        // a real row, so the tool correctly falls back to the cross-partition
        // query, which finds nothing, and returns null.
        //
        // Fixture: a lookup row keyed "stern pinball" (manufacturer-prefix row
        // for Stern's machine titled "Pinball"), exactly as OPDB sync phase (e)
        // writes for every machine by a manufacturer whose token matches "pinball"
        // (americanpinball, pinballbrothers) or for a machine literally titled
        // "Pinball" by any manufacturer.
        var sternPinballLookup = new MachineTitleLookup
        {
            Id = "stern pinball",
            PartitionKey = "stern pinball",
        };
        sternPinballLookup.UpsertEntry("GRBN-PNBL", "stern", ["stern"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        // The original key "pokemon by stern pinball" → miss.
        lookups.GetByTitleAsync("pokemon by stern pinball", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);
        // Strip 1: "by stern pinball" → miss.
        lookups.GetByTitleAsync("by stern pinball", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);
        // Strip 2 (BUG path): "stern pinball" → would hit; the fix prevents this strip from firing.
        lookups.GetByTitleAsync("stern pinball", Arg.Any<CancellationToken>())
            .Returns(sternPinballLookup);

        var repo = Substitute.For<IMachineRepository>();
        // Cross-partition fallback: no Pokemon machine in the catalog.
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());
        // The "Pinball" machine must never be point-read via the false strip-2 hit.
        repo.GetByOpdbIdAsync("GRBN-PNBL", "stern", Arg.Any<CancellationToken>())
            .Returns(BuildMachine("GRBN-PNBL", "stern", "Stern Pinball", "Pinball", 1988));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Pokemon by Stern Pinball", CancellationToken.None);

        // Must return null — there is no Pokemon machine. The "Pinball" machine
        // must NOT be returned due to the strip-2 false match.
        Assert.Null(result);
        // The "stern pinball" row must never be consulted — strip=2 is disallowed.
        await repo.DidNotReceive().GetByOpdbIdAsync("GRBN-PNBL", "stern", Arg.Any<CancellationToken>());
    }

    // ── Cross-group title collision tests (ADR-0029 follow-up 2026-06-10) ─

    [Fact]
    public async Task GetMachineByTitleAsync_ThreeEntryCollisionRow_ContentIntrinsicTieBreak_NewerYearWins()
    {
        // Real "godzilla" row: [Sega G5po2-MeP6B, Stern GweeP-MW95j, Stern GweeP-Ml9pZ].
        // Bare "godzilla" scores 0 for all entries → content-intrinsic tie-break.
        // All three have equal completeness (Year + ManufacturerDisplayName = 2);
        // Year tiebreak: Stern 2021 > Sega 1998. Among the two tied Stern entries
        // (same Year), insertion order decides: GweeP-MW95j (index 1) wins over
        // GweeP-Ml9pZ (index 2). Primary = Stern Pro.
        //
        // TitleCollisions must contain only Sega (different group G5po2 vs primary GweeP).
        // GweeP-Ml9pZ (same group GweeP) is excluded from TitleCollisions and
        // surfaced via Siblings instead.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("godzilla"),
        };
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        lookup.UpsertEntry("GweeP-Ml9pZ", "stern", ["stern"]);

        var segaMachine = new Machine
        {
            Id = "G5po2-MeP6B", PartitionKey = "sega",
            ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
            GroupId = "G5po2",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sternPro = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sternPremiumLe = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
            GroupId = "GweeP",
            EditionLabel = "Premium/LE",
            EditionTokens = ["premium", "le"],
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega",  Arg.Any<CancellationToken>()).Returns(segaMachine);
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>()).Returns(sternPro);
        repo.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>()).Returns(sternPremiumLe);
        // Primary = Stern Pro (GroupId="GweeP") → sibling fetch on GweeP.
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(sternPro, sternPremiumLe));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("godzilla", CancellationToken.None);

        // Content-intrinsic tie-break: Stern Pro wins (newer Year, first among equals).
        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Equal("Stern Pinball", result.Manufacturer);

        // Sega (different group G5po2) is in TitleCollisions for disambiguation.
        Assert.Single(result.TitleCollisions);
        Assert.Equal("G5po2-MeP6B", result.TitleCollisions[0].OpdbId);
        Assert.Equal(1998, result.TitleCollisions[0].Year);

        // SternPremiumLe (same group GweeP) is in Siblings — not in TitleCollisions.
        Assert.Single(result.Siblings);
        Assert.Equal("GweeP-Ml9pZ", result.Siblings[0].OpdbId);
        Assert.Equal("Premium/LE", result.Siblings[0].EditionLabel);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_SameGroupEntries_AreNotDuplicatedIntoTitleCollisions()
    {
        // Build a collision row where two entries share the primary's GroupId
        // (same-group siblings). They must appear in Siblings only, not in
        // TitleCollisions — TitleCollisions is for CROSS-group machines.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("godzilla"),
        };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);  // index 0 — primary
        lookup.UpsertEntry("GweeP-Ml9pZ", "stern", ["stern"]);  // index 1 — same group

        var primary = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sameGroupMachine = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla Premium/LE", Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>()).Returns(primary);
        repo.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>()).Returns(sameGroupMachine);
        // GetSiblingsByGroupIdAsync returns both machines in the same group.
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary, sameGroupMachine));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);

        // Same-group machine appears in Siblings — NOT in TitleCollisions.
        Assert.Single(result.Siblings);
        Assert.Equal("GweeP-Ml9pZ", result.Siblings[0].OpdbId);
        Assert.Empty(result.TitleCollisions);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionFetchReturnsNull_SkippedAndTieBreakContinues()
    {
        // Stale lookup-row entries (null fetch) are skipped during the content-
        // intrinsic tie-break (ADR-0049) — the tie-break continues with the
        // remaining candidates. The main result is determined by the survivors.
        //
        // Fixture: three entries, all score 0 on bare "godzilla".
        //   index 0 — Sega (1998): Year + ManufacturerDisplayName → completeness 2
        //   index 1 — Stale (null fetch): skipped during tie-break
        //   index 2 — SternPro (2021): Year + ManufacturerDisplayName → completeness 2
        //
        // After skipping the stale entry, Sega vs SternPro: same completeness, but
        // SternPro Year=2021 > Sega Year=1998 → SternPro wins.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("godzilla"),
        };
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);   // index 0
        lookup.UpsertEntry("GweeP-STALE", "stern", ["stern"]);   // index 1 — stale
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);   // index 2

        var segaMachine = new Machine
        {
            Id = "G5po2-MeP6B", PartitionKey = "sega",
            ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
            GroupId = "G5po2",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sternPro = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega",  Arg.Any<CancellationToken>()).Returns(segaMachine);
        repo.GetByOpdbIdAsync("GweeP-STALE", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>()).Returns(sternPro);
        // Primary = SternPro (GroupId="GweeP") → sibling fetch on GweeP.
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(sternPro));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("godzilla", CancellationToken.None);

        // SternPro wins (newer Year; stale entry was skipped without breaking resolution).
        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);

        // Sega is surfaced via TitleCollisions (different group G5po2).
        // Stale entry is not in TitleCollisions — it was null-fetched and skipped.
        Assert.Single(result.TitleCollisions);
        Assert.Equal("G5po2-MeP6B", result.TitleCollisions[0].OpdbId);
    }

    // ── ADR-0049 Phase 1: content-intrinsic tie-break ─────────────────────────
    // When all collision-row entries tie on token-overlap score (the "bare
    // franchise" case), the tool fetches the tied candidates and ranks them by:
    //   (a) MachineCompleteness.Score — higher wins,
    //   (b) Year descending — newer wins,
    //   (c) insertion order — deterministic final fallback.

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionTie_EqualCompletenessAndYear_InsertionOrderFallback()
    {
        // When all tied candidates share the same completeness score AND the
        // same Year, insertion order (lowest index) is the final deterministic
        // fallback. This pins the behavior so future callers understand it.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Space Mission"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Space Mission"),
        };
        lookup.UpsertEntry("SM-001", "williams", ["williams"]);  // index 0 — same year
        lookup.UpsertEntry("SM-002", "bally",    ["bally"]);     // index 1 — same year

        // Both machines: Year=1976, ManufacturerDisplayName set, no themes/designers.
        // Completeness = 2 each; Year = 1976 each → insertion order decides.
        var machine0 = new Machine
        {
            Id = "SM-001", PartitionKey = "williams",
            ManufacturerDisplayName = "Williams", Title = "Space Mission", Year = 1976,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var machine1 = new Machine
        {
            Id = "SM-002", PartitionKey = "bally",
            ManufacturerDisplayName = "Bally", Title = "Space Mission", Year = 1976,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Space Mission", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("SM-001", "williams", Arg.Any<CancellationToken>()).Returns(machine0);
        repo.GetByOpdbIdAsync("SM-002", "bally",    Arg.Any<CancellationToken>()).Returns(machine1);

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Space Mission", CancellationToken.None);

        // SM-001 (index 0) wins — completeness and year tie, insertion order breaks it.
        Assert.NotNull(result);
        Assert.Equal("SM-001", result!.OpdbId);
        Assert.Single(result.TitleCollisions);
        Assert.Equal("SM-002", result.TitleCollisions[0].OpdbId);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionTie_AllCandidatesFetchFail_DegradesToFirstTiedIndex()
    {
        // If every tie-break candidate fetch fails (all return null), the
        // tool degrades to the first tied index and lets the caller's subsequent
        // point-read (or cross-partition fallback) handle the null outcome.
        // This is invariant #17: degrade visibly, never fabricate.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Godzilla"),
        };
        lookup.UpsertEntry("G5po2-GONE",  "sega",  ["sega"]);   // index 0 — stale
        lookup.UpsertEntry("GweeP-GONE",  "stern", ["stern"]);   // index 1 — stale

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        // Both candidate fetches return null (stale lookup rows).
        // The cross-partition fallback also returns nothing — honest null result.
        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("G5po2-GONE",  "sega",  Arg.Any<CancellationToken>()).Returns((Machine?)null);
        repo.GetByOpdbIdAsync("GweeP-GONE",  "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        // All paths exhausted — honest null (never fabricate). The tool does
        // not throw even though tie-break and every fallback path degraded.
        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionTie_ManufacturerQualifiedEntryNotTied_TieBreakSkipped()
    {
        // When a manufacturer qualifier IS present in the query, one entry wins
        // outright on token-overlap (score > 0) and the content-intrinsic tie-
        // break must NOT fire. This preserves the fast path for all qualified
        // lookups and prevents extra point-reads on an already-resolved query.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Stern Godzilla"),
        };
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);   // score 0 — no qualifier match
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);   // score 1 — "stern" matches

        var sternMachine = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla", Year = 2021,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var segaMachine = new Machine
        {
            Id = "G5po2-MeP6B", PartitionKey = "sega",
            ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
            GroupId = "G5po2",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Stern Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        // Track GetByOpdbIdAsync call count to verify exactly one call for the
        // non-tie fast path (not additional tie-break fetches).
        var fetchCount = 0;
        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns(_ => { fetchCount++; return Task.FromResult<Machine?>(sternMachine); });
        repo.GetByOpdbIdAsync("G5po2-MeP6B", "sega", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Machine?>(segaMachine));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        // Exactly one fetch for the winner — the tie-break path (which fetches
        // all tied candidates) must not have fired.
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_CrossPartitionFallback_TitleCollisionsEmpty()
    {
        // When the lookup row is missing entirely and the tool resolves via
        // the cross-partition fallback query, TitleCollisions must be empty —
        // the fallback has no row to inspect for other entries.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var fallbackMachine = new Machine
        {
            Id = "G5po2-MeP6B", PartitionKey = "sega",
            ManufacturerDisplayName = "Sega", Title = "Godzilla", Year = 1998,
            GroupId = "G5po2",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(fallbackMachine));
        repo.GetSiblingsByGroupIdAsync("G5po2", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(fallbackMachine));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("G5po2-MeP6B", result!.OpdbId);
        Assert.Empty(result.TitleCollisions);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_SingleEntryRow_TitleCollisionsEmptyNoExtraPointReads()
    {
        // A single-entry lookup row has nothing to collide with.
        // TitleCollisions must be empty and GetByOpdbIdAsync must be called
        // exactly once (for the primary) — no extra reads for non-existent
        // collision entries.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("foo fighters"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("foo fighters"),
        };
        lookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]);

        var getByOpdbIdCallCount = 0;
        var machine = NewMachine("GRBN-MQR4P", "Foo Fighters", 2023);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                getByOpdbIdCallCount++;
                return Task.FromResult<Machine?>(machine);
            });
        repo.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.TitleCollisions);
        // Exactly one GetByOpdbIdAsync call — for the primary only.
        Assert.Equal(1, getByOpdbIdCallCount);
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

    // ── Forgiving resolution (ADR-0048) ────────────────────────────────────
    // getMachineByTitle must resolve real-user phrasings that the exact
    // point-read path cannot: "&"/"and" spelling differences and nickname /
    // partial-title queries. These fire only after every exact path misses.

    [Fact]
    public void GenerateConnectiveVariants_AndSpelledOut_YieldsAmpersandVariant()
    {
        var variants = MachineGroundingTool.GenerateConnectiveVariants("Dungeons and Dragons").ToList();
        Assert.Contains("Dungeons & Dragons", variants);
    }

    [Fact]
    public void GenerateConnectiveVariants_Ampersand_YieldsAndVariant()
    {
        var variants = MachineGroundingTool.GenerateConnectiveVariants("Willy Wonka & The Chocolate Factory").ToList();
        Assert.Contains("Willy Wonka and The Chocolate Factory", variants);
    }

    [Fact]
    public void GenerateConnectiveVariants_NoConnective_YieldsNothing()
    {
        // Must NOT rewrite "and"/"&" embedded inside a word (Sandman, AT&T-style).
        Assert.Empty(MachineGroundingTool.GenerateConnectiveVariants("Sandman").ToList());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_AndSpelledOut_ResolvesAmpersandCatalogTitle()
    {
        // Catalog stores "Dungeons & Dragons"; the lookup row is keyed on the
        // "&" spelling. A user typing "Dungeons and Dragons" must resolve via
        // the "&"/"and" variant retry — not silently miss, not a fuzzy scan.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Dungeons & Dragons"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Dungeons & Dragons"),
        };
        lookup.UpsertEntry("G4JBP-MJ6jr", "bally", ["bally"]);

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Dungeons and Dragons", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);
        lookups.GetByTitleAsync("Dungeons & Dragons", Arg.Any<CancellationToken>())
            .Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("G4JBP-MJ6jr", "bally", Arg.Any<CancellationToken>())
            .Returns(BuildMachine("G4JBP-MJ6jr", "bally", "Bally", "Dungeons & Dragons", 1987));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Dungeons and Dragons", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("G4JBP-MJ6jr", result!.OpdbId);
        // The fast variant retry resolved it — no fuzzy substring scan needed.
        repo.DidNotReceiveWithAnyArgs().SearchByTitleContainsAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_Nickname_ResolvesViaFuzzyFallback_SingleGroup()
    {
        // "Wonka" is a nickname — no exact / variant / prefix-strip key exists.
        // The forgiving fuzzy fallback substring-searches machine titles and
        // finds the single "Willy Wonka & The Chocolate Factory" OPDB group.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var wonkaStd = new Machine
        {
            Id = "GYWBZ-MkPrr", PartitionKey = "jjp",
            ManufacturerDisplayName = "Jersey Jack Pinball",
            Title = "Willy Wonka & The Chocolate Factory", Year = 2019, GroupId = "GYWBZ",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var wonkaLe = new Machine
        {
            Id = "GYWBZ-MW9B0", PartitionKey = "jjp",
            ManufacturerDisplayName = "Jersey Jack Pinball",
            Title = "Willy Wonka & The Chocolate Factory", Year = 2019, GroupId = "GYWBZ",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.SearchByTitleContainsAsync("wonka", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(wonkaStd, wonkaLe));
        repo.GetSiblingsByGroupIdAsync("GYWBZ", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(wonkaStd, wonkaLe));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Wonka", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Willy Wonka & The Chocolate Factory", result!.Title);
        Assert.Equal("GYWBZ", result.GroupId);
        // Single OPDB group → no cross-group ambiguity → agent answers directly.
        Assert.Empty(result.TitleCollisions);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_AmbiguousNickname_SurfacesFuzzyTitleCollisions()
    {
        // A token that substring-matches machines in two DIFFERENT OPDB groups
        // must ground a primary AND surface the other group as a TitleCollision
        // so the agent asks a clarifying question instead of silently guessing.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var ballyDnd = new Machine
        {
            Id = "G4JBP-MJ6jr", PartitionKey = "bally", ManufacturerDisplayName = "Bally",
            Title = "Dungeons & Dragons", Year = 1987, GroupId = "G4JBP",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sternDnd = new Machine
        {
            Id = "GK1Ej-MwNZr", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
            Title = "Dungeons & Dragons: The Tyrant's Eye", Year = 2025, GroupId = "GK1Ej",
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.SearchByTitleContainsAsync("dragons", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(ballyDnd, sternDnd));
        repo.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Dragons", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("G4JBP-MJ6jr", result!.OpdbId);
        var collision = Assert.Single(result.TitleCollisions);
        Assert.Equal("GK1Ej-MwNZr", collision.OpdbId);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_FuzzyNoCandidates_ReturnsNull()
    {
        // No exact key, no fuzzy substring hit → the tool must still refuse
        // honestly (null), never fabricate a match.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.SearchByTitleContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Nonexistent Machine Xyz", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_FuzzySearchThrows_EmitsAiToolErrorsAndReturnsNull()
    {
        // Invariant #17: the forgiving fuzzy fallback is best-effort. A Cosmos
        // failure during the substring search must NOT throw — it degrades to
        // an honest "no match" (null) AND meters
        // AiToolErrors{tool=getMachineByTitle, reason=fuzzy_search_unavailable}
        // so the degraded path is visible on dashboards, never silent.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        // The fuzzy substring scan throws a Cosmos-style exception mid-stream.
        repo.SearchByTitleContainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThrowingAsyncEnumerable<Machine>(new InvalidOperationException("Cosmos 503")));

        var bag = new ConcurrentBag<(string Tool, string Reason)>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "pinwiz.ai.tool_errors_total") return;
            string? tool = null, reason = null;
            foreach (var t in tags)
            {
                if (t.Key == "tool") tool = t.Value as string;
                else if (t.Key == "reason") reason = t.Value as string;
            }
            bag.Add((tool ?? "", reason ?? ""));
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.AiToolErrors);

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Wonka", CancellationToken.None);

        // Degrades to an honest refusal — never throws, never fabricates.
        Assert.Null(result);
        // The degraded path is metered, not silent.
        Assert.Contains(bag, e => e.Tool == "getMachineByTitle" && e.Reason == "fuzzy_search_unavailable");
    }

    // ── Invariant #17 audit 2026-06-12: item 2 ─────────────────────────────
    // ResolveSiblingsAsync: Cosmos failure → AiToolErrors counter increments
    // with reason=siblings_unavailable and tool=getMachineByTitle.

    [Fact]
    public async Task GetMachineByTitleAsync_SiblingFetchFails_EmitsAiToolErrorsWithSiblingsUnavailableReason()
    {
        // The primary machine resolves correctly; GetSiblingsByGroupIdAsync throws
        // a Cosmos-style exception. The tool must still return a valid DTO (no
        // abort) and must increment AiToolErrors{tool=getMachineByTitle,
        // reason=siblings_unavailable}.
        var primaryMachine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Foo Fighters",
            Year = 2023,
            GroupId = "GRBN-GRP1",   // non-empty GroupId triggers sibling fetch
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookupRow = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Foo Fighters"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Foo Fighters"),
            OpdbIds = ["GRBN-MQR4P"],
            Manufacturers = ["stern"],
        };

        var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
        titleLookups
            .GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(lookupRow);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>())
            .Returns(primaryMachine);
        // Sibling fetch throws an unexpected exception (e.g. Cosmos 503).
        repo.GetSiblingsByGroupIdAsync("GRBN-GRP1", Arg.Any<CancellationToken>())
            .Returns(ThrowingAsyncEnumerable<Machine>(new InvalidOperationException("Cosmos 503")));

        var bag = new ConcurrentBag<(string Tool, string Reason)>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "pinwiz.ai.tool_errors_total") return;
            string? tool = null, reason = null;
            foreach (var t in tags)
            {
                if (t.Key == "tool") tool = t.Value as string;
                else if (t.Key == "reason") reason = t.Value as string;
            }
            bag.Add((tool ?? "", reason ?? ""));
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.AiToolErrors);

        var tool = new MachineGroundingTool(repo, titleLookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        // Primary result must still be returned despite the sibling failure.
        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        Assert.Empty(result.Siblings); // degraded to empty sibling list

        // Counter must have fired with the expected tags.
        Assert.Contains(bag, e => e.Tool == "getMachineByTitle" && e.Reason == "siblings_unavailable");
    }

    // ── Invariant #17 audit 2026-06-12: item 3 ─────────────────────────────
    // ResolveTitleCollisionsAsync: collision candidate not found → Warning log.

    [Fact]
    public async Task GetMachineByTitleAsync_CollisionCandidateMissing_LogsWarning()
    {
        // Lookup row has two entries for the same title (cross-group collision).
        // The primary resolves; the collision candidate row returns null (stale
        // lookup). The tool must log a Warning about the missing candidate.
        var primaryMachine = new Machine
        {
            Id = "GRBN-STERN",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla",
            Year = 2021,
            GroupId = "GRBN-GROUP",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookupRow = new MachineTitleLookup
        {
            Id = "godzilla",
            PartitionKey = "godzilla",
            OpdbIds = ["GRBN-STERN", "SEGA-GODZILLA"],
            Manufacturers = ["stern", "sega"],
        };

        var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
        titleLookups
            .GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(lookupRow);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-STERN", "stern", Arg.Any<CancellationToken>())
            .Returns(primaryMachine);
        // Collision candidate is missing (stale lookup) — returns null.
        repo.GetByOpdbIdAsync("SEGA-GODZILLA", "sega", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        var loggedWarnings = new List<string>();
        var logger = new CapturingLoggerForGrounding();

        var tool = new MachineGroundingTool(repo, titleLookups, logger);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-STERN", result!.OpdbId);
        // Warning must have been logged about the missing collision candidate.
        Assert.True(logger.WarningCount > 0,
            "Expected at least one Warning log for the missing collision candidate.");
    }

    // Simple capturing logger for the Grounding collision-warning test.
    private sealed class CapturingLoggerForGrounding : Microsoft.Extensions.Logging.ILogger<MachineGroundingTool>
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                WarningCount++;
            }
        }
    }

    // Creates an IAsyncEnumerable that throws on the first MoveNextAsync call.
    // Used to simulate Cosmos SDK exceptions from streaming sibling queries.
    private static async IAsyncEnumerable<T> ThrowingAsyncEnumerable<T>(Exception ex)
    {
        await Task.CompletedTask;
        throw ex;
#pragma warning disable CS0162 // unreachable — satisfies the compiler's async iterator requirement
        yield break;
#pragma warning restore CS0162
    }
}